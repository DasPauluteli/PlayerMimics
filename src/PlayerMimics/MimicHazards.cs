using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace PlayerMimics;

// Host only. Landmines and spider webs, the way a player deals with them: it walks around the ones it noticed and
// sets off the ones it missed. Vanilla enemies never trigger either, so both are done here.
internal sealed partial class MimicBrain
{
    private const float MineTriggerRange = 0.75f; // standing this close (flat) presses a mine
    private const float MineAvoidRange = 1.6f;    // how wide it walks around one it knows about
    private const float WebAvoidRange = 0.9f;
    private const float NoticeRange = 8f;         // hazards further away aren't looked at yet

    // Shared by all brains: found again every couple of seconds.
    private static readonly List<Landmine> mines = new();
    private static readonly List<SandSpiderWebTrap> webs = new();
    private static float hazardsFoundAt = -100f;

    private readonly HashSet<Object> noticedHazards = new();
    private readonly Dictionary<Object, int> hazardLooks = new(); // how often it had the chance to notice one
    private Landmine? mineUnderfoot;
    private float frozenOnMineUntil;
    private SandSpiderWebTrap? stuckInWeb;
    private float stuckUntil;
    private readonly HashSet<SandSpiderWebTrap> trippedWebs = new();
    private Vector3? detour;
    private Vector3 detourFor;
    private readonly NavMeshPath hazardPath = new();

    private static void FindHazards()
    {
        if (Time.time - hazardsFoundAt < 2f) return;
        hazardsFoundAt = Time.time;
        mines.Clear();
        mines.AddRange(Object.FindObjectsOfType<Landmine>());
        webs.Clear();
        foreach (var spider in Object.FindObjectsOfType<SandSpiderAI>())
        {
            foreach (var web in spider.webTraps)
            {
                if (web != null) webs.Add(web);
            }
        }
    }

    private static bool Live(Landmine m) => m != null && !m.hasExploded && m.mineActivated;

    // A web runs from where the spider sat to where it shot it (the web object looks at that end).
    private static (Vector3 a, Vector3 b) WebEnds(SandSpiderWebTrap w)
    {
        var root = w.transform.parent != null ? w.transform.parent : w.transform;
        return (root.position, root.position + root.forward * (w.zScale * 4f));
    }

    // ---- noticing (AI tick)

    // Each hazard gets a look when it comes into view, and one more up close. A careful walker almost always sees a
    // mine's blinking light; webs are easier to miss, and running or fighting makes both easier to miss.
    private void NoticeHazards()
    {
        FindHazards();
        bool hurried = isSprinting || Current is Behaviour.Attack or Behaviour.Flee;
        float miss = Mathf.Clamp01(Cfg.HazardMistakes.Value * (hurried ? 2.5f : 1f));
        foreach (var m in mines)
        {
            if (!Live(m) || noticedHazards.Contains(m)) continue;
            Look(m, m.transform.position + Vector3.up * 0.1f, miss);
        }
        foreach (var w in webs)
        {
            if (w == null || noticedHazards.Contains(w)) continue;
            Look(w, w.centerOfWeb.position, Mathf.Clamp01(miss * 1.8f));
        }
    }

    private void Look(Object hazard, Vector3 point, float missChance)
    {
        float d = Vector3.Distance(transform.position, point);
        hazardLooks.TryGetValue(hazard, out int looks);
        bool due = (looks == 0 && d < NoticeRange) || (looks == 1 && d < 3f);
        if (!due) return;
        if (Physics.Linecast(Head, point, StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore)) return;
        hazardLooks[hazard] = looks + 1;
        if (Random.value < missChance) return;
        noticedHazards.Add(hazard);
        detour = null; // plan the way again with this one in mind
        if (hazard is Landmine && AlivePlayers().Any(p => IsAudience(p) && SameSide(p) && Dist(p) < 15f))
        {
            AddEvent($"you noticed a landmine {d:0} m away (walk around it; you could warn the others)", "mine-seen", 30f);
        }
    }

    // ---- walking around them (from the DoAIInterval patch, before the agent gets its destination)

    internal Vector3 HazardSafeDestination(Vector3 target)
    {
        if (Time.time < frozenOnMineUntil) return transform.position; // froze on the click, like a player would
        if (noticedHazards.Count == 0) return target;
        noticedHazards.RemoveWhere(h => h == null || (h is Landmine m && !Live(m)));

        // Never aim at a spot on a known mine (following a player who stands next to one, say).
        foreach (var h in noticedHazards)
        {
            if (h is not Landmine m) continue;
            Vector3 c = m.transform.position;
            if (FlatDistance(target, c) < MineAvoidRange && Mathf.Abs(target.y - c.y) < 2f)
            {
                Vector3 away = Flat(target - c);
                if (away.sqrMagnitude < 0.01f) away = Flat(transform.position - c);
                if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
                if (TryNavPoint(c + away.normalized * (MineAvoidRange + 0.3f), 1.5f, out var moved)) target = moved;
            }
        }

        if (detour.HasValue)
        {
            if (FlatDistance(transform.position, detour.Value) < 0.8f || FlatDistance(target, detourFor) > 2.5f) detour = null;
            else return detour.Value;
        }
        if (!Masked.agent.isOnNavMesh || !NavMesh.CalculatePath(transform.position, target, NavMesh.AllAreas, hazardPath))
        {
            return target;
        }

        // The first known hazard on the next 15 m of the way, and a point beside it to walk to first.
        var corners = hazardPath.corners;
        float walked = 0f;
        for (int i = 1; i < corners.Length && walked < 15f; i++)
        {
            Vector3 a = corners[i - 1], b = corners[i];
            walked += Vector3.Distance(a, b);
            foreach (var h in noticedHazards)
            {
                Vector3? around = h switch
                {
                    Landmine m => AroundMine(a, b, m.transform.position),
                    SandSpiderWebTrap w => AroundWeb(a, b, w),
                    _ => null,
                };
                if (around == null) continue;
                detour = around;
                detourFor = target;
                return around.Value;
            }
        }
        return target;
    }

    private Vector3? AroundMine(Vector3 a, Vector3 b, Vector3 mine)
    {
        if (Mathf.Abs(mine.y - Closest(a, b, mine).y) > 1.5f) return null;
        if (FlatDistance(Closest(a, b, mine), mine) > MineAvoidRange) return null;
        Vector3 along = Flat(b - a).normalized;
        Vector3 side = Vector3.Cross(Vector3.up, along);
        // Pass on the side the path already leans to; try the other one if that's a wall.
        float lean = Vector3.Dot(Flat(Closest(a, b, mine) - mine), side) >= 0f ? 1f : -1f;
        foreach (float s in new[] { lean, -lean })
        {
            if (TryNavPoint(mine + side * (s * (MineAvoidRange + 0.6f)), 0.8f, out var p) && SafeSpot(p)) return p;
        }
        return null;
    }

    // Around the end of a web when there's room; a web across the whole corridor has no way around, so it walks on.
    private Vector3? AroundWeb(Vector3 a, Vector3 b, SandSpiderWebTrap w)
    {
        var (s, e) = WebEnds(w);
        if (!Crosses(Flat(a), Flat(b), Flat(s), Flat(e))) return null;
        if (Mathf.Abs(Closest(a, b, w.centerOfWeb.position).y - w.centerOfWeb.position.y) > 2.5f) return null;
        Vector3 dir = Flat(e - s).normalized;
        Vector3? best = null;
        float bestDist = float.MaxValue;
        foreach (var end in new[] { s - dir * WebAvoidRange, e + dir * WebAvoidRange })
        {
            if (!TryNavPoint(end, 0.6f, out var p) || !SafeSpot(p)) continue;
            if (Crosses(Flat(transform.position), Flat(p), Flat(s), Flat(e))) continue;
            float d = FlatDistance(transform.position, p);
            if (d < bestDist)
            {
                best = p;
                bestDist = d;
            }
        }
        return best;
    }

    private bool SafeSpot(Vector3 p)
    {
        foreach (var h in noticedHazards)
        {
            if (h is Landmine m && FlatDistance(p, m.transform.position) < MineAvoidRange) return false;
        }
        return true;
    }

    private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

    private static Vector3 Closest(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        float t = ab.sqrMagnitude < 1e-4f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
        return a + ab * t;
    }

    private static bool Crosses(Vector3 p1, Vector3 p2, Vector3 q1, Vector3 q2)
    {
        static float Cross(Vector3 o, Vector3 a, Vector3 b) => (a.x - o.x) * (b.z - o.z) - (a.z - o.z) * (b.x - o.x);
        float d1 = Cross(q1, q2, p1), d2 = Cross(q1, q2, p2), d3 = Cross(p1, p2, q1), d4 = Cross(p1, p2, q2);
        return d1 * d2 < 0f && d3 * d4 < 0f;
    }

    // ---- stepping on them (every frame)

    private void HazardStep()
    {
        Vector3 pos = transform.position;

        // A mine clicks when it steps on it and goes off when it steps off, like for players.
        if (mineUnderfoot != null)
        {
            if (!Live(mineUnderfoot))
            {
                mineUnderfoot = null;
            }
            else if (FlatDistance(pos, mineUnderfoot.transform.position) > MineTriggerRange + 0.35f)
            {
                var mine = mineUnderfoot;
                mineUnderfoot = null;
                mine.TriggerMineOnLocalClientByExiting(); // the host's own call: sets it off for everyone
                Plugin.Log.LogDebug($"{Copied.playerUsername}'s Masked set off a mine");
            }
        }
        else
        {
            foreach (var m in mines)
            {
                if (!Live(m) || m.pressMineDebounceTimer > 0f) continue;
                Vector3 c = m.transform.position;
                if (FlatDistance(pos, c) > MineTriggerRange || Mathf.Abs(pos.y - c.y) > 1f) continue;
                mineUnderfoot = m;
                m.pressMineDebounceTimer = 0.5f;
                m.PressMineServerRpc(); // the click everyone hears
                noticedHazards.Add(m);
                frozenOnMineUntil = Time.time + Random.Range(1f, 2.5f);
                AddEvent("you stepped on a landmine and it clicked! It goes off when you step off", "mine-click", 3f, decideNow: true);
                break;
            }
        }

        // A web holds it a few seconds (struggling, slowed down), then tears.
        if (stuckInWeb != null)
        {
            if (Time.time < stuckUntil) return;
            var web = stuckInWeb;
            stuckInWeb = null;
            BreakWeb(web);
            return;
        }
        Vector3 chest = pos + Vector3.up * 0.9f;
        foreach (var w in webs)
        {
            if (w == null || w.mainScript == null || trippedWebs.Contains(w)) continue;
            if (Vector3.Distance(w.centerOfWeb.position, chest) > 8f) continue;
            var col = w.GetComponent<Collider>();
            bool inside = col != null ? Vector3.Distance(col.ClosestPoint(chest), chest) < 0.35f : WebTouches(w, pos);
            if (!inside) continue;
            trippedWebs.Add(w);
            noticedHazards.Add(w);
            stuckInWeb = w;
            stuckUntil = Time.time + Random.Range(1.5f, 3.5f);
            ModNetwork.Instance?.WebClientRpc(new NetworkObjectReference(w.mainScript.NetworkObject), w.trapID, false);
            AddEvent("you walked into a spider web and got stuck in it", "web", 5f, decideNow: true);
            break;
        }
    }

    private static bool WebTouches(SandSpiderWebTrap w, Vector3 pos)
    {
        var (s, e) = WebEnds(w);
        Vector3 c = Closest(s, e, pos);
        return FlatDistance(c, pos) < 0.4f && pos.y > Mathf.Min(s.y, e.y) - 1.5f && pos.y < Mathf.Max(s.y, e.y) + 0.5f;
    }

    private static void BreakWeb(SandSpiderWebTrap w)
    {
        if (w.mainScript == null || ModNetwork.Instance == null) return;
        ModNetwork.Instance.WebClientRpc(new NetworkObjectReference(w.mainScript.NetworkObject), w.trapID, true);
    }

    // Stuck in a web: players move at about a third of their speed there.
    private float HazardSpeedFactor => stuckInWeb != null ? 0.35f : 1f;
}
