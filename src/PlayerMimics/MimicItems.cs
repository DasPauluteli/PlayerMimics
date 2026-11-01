using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace PlayerMimics;

// Host only. Items and doors, done like a player: what it sees lying around and what it carries (for the LLM), and the
// steps for picking things up, switching, dropping or handing them over, carrying loot back to the ship and going
// through entrances, at a player's pace. Each grab, switch, drop and door goes to every client through ModNetwork.
internal sealed partial class MimicBrain
{
    private MimicInventory Inventory => Disguise.Inventory!;

    private GrabbableObject? taskItem;
    private PlayerControllerB? giveTo;
    private bool dropAll;
    private bool dropScrapOnly;
    private EntranceTeleport? taskDoor;
    private float stepAt; // the next step waits until then: reacting, a beat with the item in hand, between drops
    private float finishAt; // done; stands still until then (the grab or drop animation), then goes back to what it was doing
    private float taskReach = 1.4f;
    private float lastTaskPath = -100f;
    private bool shipArrived;
    private Vector3 shipSpot;
    private bool taskDone; // got there and is just waiting (in the ship): any decision may end it
    private float shipRoomSince = -1f;
    private Behaviour resumeBehaviour = Behaviour.BlendIn;
    private PlayerControllerB? resumeTarget;
    private bool droppedOnDeath;

    // After going through a door on purpose ("you go first"), it waits on that side instead of following the others
    // back out.
    private float sideLockUntil;
    private bool sideLockInside;
    private float doorArriveAt = -1f;
    private float lastDoorUse = -100f;

    // The door GoTo is heading for, to reach a point on the other side.
    private EntranceTeleport? routeDoor;
    private Vector3 routeFor;
    private float routePauseAt = -1f;

    private static readonly List<GrabbableObject> items = new();
    private static float itemsFound = -100f;
    private static readonly List<EntranceTeleport> doors = new();
    private static float doorsFound = -100f;

    private static bool IsTask(Behaviour b) => b is Behaviour.PickUp or Behaviour.Drop or Behaviour.Entrance or Behaviour.GoToShip;

    // ---- the world

    private static List<GrabbableObject> AllItems()
    {
        if (Time.time - itemsFound < 2f) return items;
        itemsFound = Time.time;
        items.Clear();
        items.AddRange(Object.FindObjectsOfType<GrabbableObject>());
        return items;
    }

    // Entrances that lead somewhere, each with its other end.
    private static List<EntranceTeleport> Doors()
    {
        if (Time.time - doorsFound < 5f && doors.All(d => d != null)) return doors;
        doorsFound = Time.time;
        doors.Clear();
        foreach (var d in Object.FindObjectsOfType<EntranceTeleport>())
        {
            if (d.entrancePoint == null) continue;
            if (d.exitScript == null) d.FindExitPoint();
            if (d.exitScript != null && d.exitScript.entrancePoint != null) doors.Add(d);
        }
        return doors;
    }

    // Doors on its side: the outside ones lead in, the inside ones lead out.
    private IEnumerable<EntranceTeleport> DoorsHere() => Doors().Where(d => d.isEntranceToBuilding == Masked.isOutside);

    private static string DoorName(EntranceTeleport d) => d.entranceId == 0 ? "the main entrance" : "a fire exit";

    private bool InShipRoom()
    {
        var round = StartOfRound.Instance;
        return round.shipInnerRoomBounds != null && round.shipInnerRoomBounds.bounds.Contains(transform.position + Vector3.up * 0.3f);
    }

    // Lying around where a player could pick it up.
    private static bool Loose(GrabbableObject? item) =>
        item != null && item.itemProperties != null && item.grabbable && !item.deactivated && !item.isHeld && !item.isHeldByEnemy &&
        !item.isPocketed && !item.heldByPlayerOnServer && item.parentObject == null && item.NetworkObject != null &&
        item.NetworkObject.IsSpawned && item is not RagdollGrabbableObject &&
        (item.transform.parent == null || item.transform.parent.GetComponentInParent<DepositItemsDesk>() == null);

    // The apparatus still in its machine: pulling it out cuts the facility's power.
    private static bool DockedApparatus(GrabbableObject item) => item is LungProp { isLungDocked: true };

    // What it sees from where it stands, nearest first. Like a player looking around: things behind it only when close.
    private List<GrabbableObject> ItemsInView(int max = 6)
    {
        Vector3 eye = Head;
        var seen = new List<(GrabbableObject item, float d)>();
        foreach (var item in AllItems())
        {
            if (!Loose(item)) continue;
            Vector3 pos = item.transform.position;
            float d = Vector3.Distance(transform.position, pos);
            if (d > 18f) continue;
            Vector3 flat = pos - transform.position;
            flat.y = 0f;
            if (d > 8f && flat.sqrMagnitude > 0.01f && Vector3.Angle(transform.forward, flat) > 110f) continue;
            Vector3 aim = pos + Vector3.up * 0.2f;
            if (Physics.Linecast(eye, aim, out RaycastHit hit, StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore) &&
                Vector3.Distance(hit.point, aim) > (DockedApparatus(item) ? 0.8f : 0.3f))
            {
                continue; // blocked before it; the machine the apparatus sits in doesn't count
            }
            seen.Add((item, d));
        }
        return seen.OrderBy(s => s.d).Take(max).Select(s => s.item).ToList();
    }

    private static string Describe(GrabbableObject item)
    {
        var p = item.itemProperties;
        var parts = new List<string>();
        if (p.isScrap) parts.Add($"${item.scrapValue}");
        if (p.twoHanded) parts.Add("two-handed");
        int lb = MimicInventory.Pounds(p.weight);
        if (lb > 0) parts.Add($"{lb} lb");
        return parts.Count > 0 ? $"{p.itemName} ({string.Join(", ", parts)})" : p.itemName;
    }

    private JObject ItemJson(string id, GrabbableObject item)
    {
        var p = item.itemProperties;
        var o = new JObject
        {
            ["id"] = id,
            ["item"] = p.itemName,
            ["distance_m"] = Mathf.RoundToInt(Vector3.Distance(transform.position, item.transform.position)),
        };
        if (p.isScrap) o["value"] = item.scrapValue;
        else o["tool"] = true;
        int lb = MimicInventory.Pounds(p.weight);
        if (lb > 0) o["weight_lb"] = lb;
        if (p.twoHanded) o["two_handed"] = true;
        if (item.isInShipRoom) o["in_ship"] = "already collected";
        if (DockedApparatus(item)) o["note"] = "plugged into the machine; pulling it out cuts the power to the building";
        if (!Inventory.CanTake(item, out string why)) o["cant_pick_up"] = why;
        return o;
    }

    private JToken InventoryJson()
    {
        var list = new JArray();
        foreach (var (slot, item) in Inventory.Items)
        {
            string name = slot == MimicInventory.Belt ? "belt" : $"slot{slot + 1}";
            list.Add($"{name}: {Describe(item)}{(slot == Inventory.Current ? " (in your hands)" : " (pocketed)")}");
        }
        if (list.Count == 0) return "empty: 4 slots and the utility belt are free";
        return list;
    }

    // What the LLM gets about items: what it sees (with ids to name in "item") and what it carries.
    private void AddItemContext(JObject you, JObject request, JArray allowed, out Dictionary<string, GrabbableObject> view)
    {
        view = new Dictionary<string, GrabbableObject>();
        var seen = new JArray();
        int n = 1;
        foreach (var item in ItemsInView())
        {
            string id = "i" + n++;
            view[id] = item;
            seen.Add(ItemJson(id, item));
        }
        var held = Inventory.Held;
        you["holding"] = held != null ? Describe(held) : "nothing";
        you["inventory"] = InventoryJson();
        int lb = MimicInventory.Pounds(Inventory.CarryWeight);
        if (lb > 0) you["carrying_weight_lb"] = lb;
        request["items_in_view"] = seen;

        if (view.Values.Any(i => Inventory.CanTake(i, out _))) allowed.Add("pick_up");
        if (!Inventory.IsEmpty)
        {
            allowed.Add("drop");
            if (Inventory.Items.Any(x => x.slot != Inventory.Current) && !Inventory.HandsFull) allowed.Add("hold");
        }
        if (DoorsHere().Any()) allowed.Add(Masked.isOutside ? "go_inside" : "go_outside");
        if (!InShipRoom() || Current == Behaviour.GoToShip) allowed.Add("go_to_ship"); // in the ship: keep waiting there
    }

    // ---- picking the item the LLM meant

    private static bool NameMatches(GrabbableObject item, string name)
    {
        string n = item.itemProperties.itemName.ToLowerInvariant();
        string q = name.Trim().ToLowerInvariant();
        return q.Length >= 3 && (n == q || n.Contains(q) || q.Contains(n));
    }

    private GrabbableObject? ResolveSeen(string itemRef, Dictionary<string, GrabbableObject> view)
    {
        string r = itemRef.Trim().ToLowerInvariant();
        // No item named ("grab that"): the closest one it sees.
        if (r is "" or "it" or "this" or "that")
        {
            return view.Values.Where(Loose).OrderBy(i => Vector3.Distance(i.transform.position, transform.position)).FirstOrDefault();
        }
        if (int.TryParse(r, out int number)) r = "i" + number;
        if (view.TryGetValue(r, out var byId)) return Loose(byId) ? byId : null;
        var near = AllItems().Where(i => Loose(i) && Vector3.Distance(i.transform.position, transform.position) < 20f && NameMatches(i, r));
        return view.Values.Where(i => Loose(i) && NameMatches(i, r)).Concat(near)
            .OrderBy(i => Vector3.Distance(i.transform.position, transform.position)).FirstOrDefault();
    }

    private GrabbableObject? ResolveCarried(string itemRef)
    {
        string r = itemRef.Trim().ToLowerInvariant();
        if (r is "" or "it" or "this" or "that" or "held" or "hand" or "hands" or "current" or "item") return Inventory.Held;
        if (r == "belt" || r == "utility belt") return MimicInventory.Live(Inventory.Slots[MimicInventory.Belt]);
        if (int.TryParse(r.Replace("slot", "").Trim(), out int n) && n >= 1 && n <= MimicInventory.Belt)
        {
            return MimicInventory.Live(Inventory.Slots[n - 1]);
        }
        return Inventory.Items.Select(x => x.item).FirstOrDefault(i => NameMatches(i, r));
    }

    // ---- starting a task from an LLM answer

    // True when the action was an item or door action and it's handled (started, already going, or a switch).
    private bool StartTask(string action, string itemRef, PlayerControllerB? target, Dictionary<string, GrabbableObject> view)
    {
        switch (action)
        {
            case "pick_up":
                {
                    var item = ResolveSeen(itemRef, view);
                    if (item == null)
                    {
                        AddEvent($"You tried to pick up \"{itemRef}\" but don't see it.", "noitem", 10f);
                        return false;
                    }
                    if (!Inventory.CanTake(item, out string why))
                    {
                        AddEvent($"You can't pick up the {item.itemProperties.itemName}: {why}.", "cantpick", 10f);
                        return false;
                    }
                    if (Current == Behaviour.PickUp && taskItem == item) return true;
                    BeginTask(Behaviour.PickUp, null, 25f);
                    taskItem = item;
                    return true;
                }
            case "drop":
                {
                    if (Inventory.IsEmpty) return false;
                    string r = itemRef.Trim().ToLowerInvariant();
                    bool all = r is "all" or "everything" or "all scrap" or "scrap" or "loot";
                    var item = all ? null : ResolveCarried(itemRef);
                    if (!all && item == null)
                    {
                        AddEvent($"You tried to drop \"{itemRef}\" but you're not carrying it.", "nodrop", 10f);
                        return false;
                    }
                    var to = target != null && SameSide(target) && Dist(target) < 30f ? target : null;
                    if (Current == Behaviour.Drop && taskItem == item && dropAll == all && giveTo == to) return true;
                    BeginTask(Behaviour.Drop, to, 20f);
                    taskItem = item;
                    dropAll = all;
                    dropScrapOnly = all && r != "all" && r != "everything";
                    giveTo = to;
                    stepAt = Time.time + Random.Range(0.15f, 0.4f);
                    return true;
                }
            case "hold":
                {
                    var item = ResolveCarried(itemRef);
                    if (item == null) return false;
                    int slot = Inventory.SlotOf(item);
                    if (slot == Inventory.Current) return true;
                    if (Inventory.HandsFull)
                    {
                        AddEvent($"You can't switch items while holding the {Inventory.Held!.itemProperties.itemName} with both hands; drop it first.",
                            "twohands", 10f);
                        return true;
                    }
                    if (Time.time - Inventory.LastChange > 0.3f) ModNetwork.Instance?.MimicSwitch(Masked, slot);
                    return true; // a slot switch doesn't change what it's doing
                }
            case "go_inside":
            case "go_outside":
                {
                    if ((action == "go_inside") != Masked.isOutside) return false; // already on that side
                    var door = DoorsHere().OrderBy(d => Vector3.Distance(transform.position, d.entrancePoint.position) + (d.entranceId == 0 ? 0f : 10f))
                        .FirstOrDefault();
                    if (door == null) return false;
                    if (Current == Behaviour.Entrance) return true;
                    BeginTask(Behaviour.Entrance, null, 60f);
                    taskDoor = door;
                    return true;
                }
            case "go_to_ship":
                {
                    if (Current == Behaviour.GoToShip) return true;
                    BeginTask(Behaviour.GoToShip, null, 150f);
                    return true;
                }
        }
        return false;
    }

    // ---- grabbing loot in passing

    private float lastPassingGrab = -100f;
    private readonly HashSet<GrabbableObject> passedOver = new();

    // AI tick: walking around with the others and there's scrap right next to it: it picks it up, like a player
    // would. Not when someone else is closer to it, not the apparatus (that's a decision), not what's already in the
    // ship, and every piece gets one chance, so it doesn't hoover up everything.
    private void GrabScrapInPassing()
    {
        if (!Cfg.GrabScrapNearPlayers.Value || Current is not (Behaviour.BlendIn or Behaviour.Follow or Behaviour.Wander) ||
            Fleeing || HuntingKnower || Masked.inSpecialAnimation || Time.time - lastPassingGrab < 12f ||
            Inventory.HandsFull || Inventory.CarryWeight > 1.35f || InShipRoom())
        {
            return;
        }
        var crew = AlivePlayers().Where(p => IsAudience(p) && SameSide(p) && Dist(p) < 12f).ToList();
        if (crew.Count == 0) return;
        foreach (var item in ItemsInView(4))
        {
            if (passedOver.Contains(item)) continue;
            Vector3 pos = item.transform.position;
            float mine = Vector3.Distance(pos, transform.position);
            if (mine > 3.5f) continue;
            if (!item.itemProperties.isScrap || item.isInShipRoom || item.isInElevator || DockedApparatus(item)) continue;
            if (!Inventory.CanTake(item, out _)) continue;
            if (crew.Any(p => Vector3.Distance(p.transform.position, pos) < mine)) continue; // theirs to take
            passedOver.Add(item);
            if (Random.value > 0.7f) continue;
            lastPassingGrab = Time.time;
            Plugin.Log.LogDebug($"{Copied.playerUsername}'s Masked grabs the {item.itemProperties.itemName} in passing");
            BeginTask(Behaviour.PickUp, null, 12f);
            taskItem = item;
            stepAt = Time.time + Random.Range(0.2f, 0.5f);
            return;
        }
    }

    // A Masked that spawned on its own starts with a piece of scrap in its hands, like someone who's been looting.
    internal void GiveStartingItem(GrabbableObject item)
    {
        int slot = Inventory.FreeSlotFor(item);
        if (slot < 0 || ModNetwork.Instance == null) return;
        item.heldByPlayerOnServer = true;
        ModNetwork.Instance.MimicGrab(Masked, item, slot);
        passedOver.Add(item);
    }

    private void BeginTask(Behaviour b, PlayerControllerB? target, float seconds)
    {
        // Remember what it was doing before the first task in a row, to go back to it.
        if (!IsTask(Current))
        {
            resumeBehaviour = Current is Behaviour.Emote or Behaviour.Flee or Behaviour.Stare ? Behaviour.BlendIn : Current;
            resumeTarget = Target;
        }
        SetBehaviour(b, target, seconds);
    }

    private void ClearTask()
    {
        taskItem = null;
        giveTo = null;
        dropAll = false;
        dropScrapOnly = false;
        taskDoor = null;
        finishAt = 0f;
        shipArrived = false;
        shipSpot = Vector3.zero;
        shipRoomSince = -1f;
        taskDone = false;
        doorArriveAt = -1f;
        routePauseAt = -1f;
        taskReach = 1.4f;
        lastTaskPath = -100f;
    }

    private void FinishTask(string? evt, Behaviour? then = null)
    {
        if (!string.IsNullOrEmpty(evt)) AddEvent(evt!, decideNow: true);
        var next = then ?? resumeBehaviour;
        bool needsTarget = next is Behaviour.Follow or Behaviour.Stare or Behaviour.Lure or Behaviour.Attack;
        var target = needsTarget ? resumeTarget : null;
        if (target != null && (target.isPlayerDead || !target.isPlayerControlled)) target = null;
        if ((needsTarget && target == null) || (next == Behaviour.Attack && !CanAttack(out _)) || IsTask(next)) next = Behaviour.BlendIn;
        SetBehaviour(next, target, DefaultDuration(next));
    }

    // Tick's timeout for a task that never finished.
    private string? TaskGaveUp() => Current switch
    {
        Behaviour.PickUp when taskItem != null => $"You couldn't get to the {taskItem.itemProperties.itemName}.",
        Behaviour.Entrance => "You couldn't get to the door.",
        Behaviour.GoToShip when !InShipRoom() => "You didn't make it back to the ship.",
        _ => null,
    };

    // ---- task steps (one per AI tick)

    private void WalkTo(Vector3 pos)
    {
        if (Time.time - lastTaskPath < 0.8f) return;
        lastTaskPath = Time.time;
        Vector3 nav = TryNavPoint(pos, 2f, out Vector3 found) ? found : pos;
        // Close enough counts from the nearest walkable spot, for things on shelves or just off the floor.
        taskReach = Mathf.Clamp(FlatDistance(nav, pos) + 0.5f, 1.4f, 2.8f);
        Masked.SetDestinationToPosition(nav);
    }

    private bool Finishing()
    {
        if (finishAt <= 0f) return false;
        Stop();
        if (Time.time >= finishAt) FinishTask(null);
        return true;
    }

    private void PickUpStep()
    {
        if (Finishing()) return;
        var item = taskItem;
        if (!Loose(item))
        {
            var by = item != null && item.isHeld ? item.playerHeldBy : null;
            FinishTask(by != null ? $"{by.playerUsername} picked up the {item!.itemProperties.itemName} before you." : null);
            return;
        }
        string name = item!.itemProperties.itemName;
        if (!Inventory.CanTake(item, out string why))
        {
            FinishTask($"You can't pick up the {name}: {why}.");
            return;
        }
        Vector3 pos = item.transform.position;
        if (FlatDistance(transform.position, pos) > taskReach || Mathf.Abs(pos.y - transform.position.y) > 2.2f)
        {
            WalkTo(pos);
            stepAt = Time.time + Random.Range(0.2f, 0.5f); // a beat after getting there, like reaching for it
            return;
        }
        Stop();
        if (!lookPoint.HasValue || Vector3.Distance(lookPoint.Value, pos) > 0.5f) LookAtPoint(pos, 1.5f);
        if (Time.time < stepAt || Time.time - Inventory.LastChange < 0.3f || ModNetwork.Instance == null) return;
        int slot = Inventory.FreeSlotFor(item);
        if (slot < 0)
        {
            FinishTask(null);
            return;
        }
        item.heldByPlayerOnServer = true; // a player's grab arriving now gets turned down, like two players grabbing at once
        ModNetwork.Instance.MimicGrab(Masked, item, slot);
        AddEvent($"You picked up the {Describe(item)}.", decideNow: true);
        float grabTime = item.itemProperties.grabAnimationTime > 0f ? item.itemProperties.grabAnimationTime : 0.4f;
        finishAt = Time.time + grabTime + 0.15f;
    }

    private void DropStep()
    {
        if (Finishing()) return;
        if (giveTo != null && (giveTo.isPlayerDead || !giveTo.isPlayerControlled)) giveTo = null;
        if (giveTo != null)
        {
            // Handing it over: walk up to them and drop it at their feet, facing them.
            if (!SameSide(giveTo) || Dist(giveTo) > 2.3f)
            {
                GoTo(giveTo.transform.position, giveTo.isInsideFactory);
                stepAt = Time.time + Random.Range(0.3f, 0.6f);
                return;
            }
            Stop();
            if (lookPlayer != giveTo) LookAtPlayer(giveTo, 2f);
        }
        else
        {
            Stop();
        }
        if (Time.time < stepAt || Time.time - Inventory.LastChange < 0.35f) return;
        var item = NextDrop();
        if (item == null)
        {
            FinishTask(null);
            return;
        }
        int slot = Inventory.SlotOf(item);
        if (slot != Inventory.Current)
        {
            if (Inventory.HandsFull)
            {
                FinishTask($"You can't switch items while holding the {Inventory.Held!.itemProperties.itemName} with both hands; drop it first.");
                return;
            }
            ModNetwork.Instance?.MimicSwitch(Masked, slot);
            stepAt = Time.time + Random.Range(0.35f, 0.7f); // a moment with it in hand before it goes down, like a player
            return;
        }
        HostDrop(item, ahead: true);
        bool more = dropAll && Inventory.Items.Any(x => x.item != item && (!dropScrapOnly || x.item.itemProperties.isScrap));
        AddEvent(giveTo != null ? $"You dropped the {item.itemProperties.itemName} for {giveTo.playerUsername}."
            : InShipRoom() && item.itemProperties.isScrap ? $"You dropped the {Describe(item)} in the ship."
            : $"You dropped the {item.itemProperties.itemName}.", decideNow: !more);
        if (more)
        {
            stepAt = Time.time + Random.Range(0.4f, 0.8f);
            return;
        }
        finishAt = Time.time + 0.4f;
    }

    // Dropping everything: what's in its hand first, then the pockets in order, like a player.
    private GrabbableObject? NextDrop()
    {
        if (!dropAll) return taskItem != null && Inventory.SlotOf(taskItem) >= 0 ? taskItem : null;
        var held = Inventory.Held;
        if (held != null && (!dropScrapOnly || held.itemProperties.isScrap)) return held;
        return Inventory.Items.Select(x => x.item).FirstOrDefault(i => !dropScrapOnly || i.itemProperties.isScrap);
    }

    // Drops it like a player (PlayerControllerB.DiscardHeldObject): a bit ahead onto the floor, or straight down when
    // standing in the ship, when dying, or for things that can't go ahead. Scrap dropped while in the ship counts.
    private void HostDrop(GrabbableObject item, bool ahead)
    {
        if (ModNetwork.Instance == null) return;
        var round = StartOfRound.Instance;
        bool standingInShip = round.shipBounds != null && round.shipBounds.bounds.Contains(transform.position + Vector3.up * 0.3f);
        Vector3 floor;
        if (ahead && !standingInShip && item.itemProperties.allowDroppingAheadOfPlayer)
        {
            var ray = new Ray(transform.position + Vector3.up * 0.4f, transform.forward);
            Vector3 start = Physics.Raycast(ray, out RaycastHit hit, 1.7f, 268438273, QueryTriggerInteraction.Ignore)
                ? ray.GetPoint(Mathf.Clamp(hit.distance - 0.3f, 0.01f, 2f))
                : ray.GetPoint(1.7f);
            floor = item.GetItemFloorPosition(start);
            if (floor == start) floor = item.GetItemFloorPosition();
        }
        else
        {
            floor = item.GetItemFloorPosition();
        }
        bool inElevator = round.shipBounds != null && round.shipBounds.bounds.Contains(floor);
        bool inShipRoom = inElevator && InShipRoom();
        Transform parent = inElevator ? round.elevatorTransform : round.propsContainer;
        ModNetwork.Instance.MimicDrop(Masked, item, parent.InverseTransformPoint(floor), inElevator, inShipRoom,
            (int)transform.localEulerAngles.y);
    }

    // A dead player drops everything; so does it.
    private void DropEverythingOnDeath()
    {
        if (droppedOnDeath || Disguise == null || Disguise.Inventory == null) return;
        droppedOnDeath = true;
        foreach (var (_, item) in Inventory.Items) HostDrop(item, ahead: false);
    }

    private void EntranceStep()
    {
        if (Finishing()) return;
        var door = taskDoor;
        if (door == null || door.entrancePoint == null || door.isEntranceToBuilding != Masked.isOutside)
        {
            FinishTask(null, Behaviour.BlendIn);
            return;
        }
        if (!AtDoor(door))
        {
            doorArriveAt = -1f;
            Masked.SetDestinationToPosition(DoorSpot(door));
            return;
        }
        Stop();
        if (doorArriveAt < 0f)
        {
            // A moment at the door, like a player holding the button.
            doorArriveAt = Time.time + Random.Range(0.4f, 0.9f);
            LookAtPoint(door.transform.position + Vector3.up * 1.4f, 1f);
        }
        if (Time.time < doorArriveAt || Time.time - lastDoorUse < 2f) return;
        bool goingIn = Masked.isOutside;
        CrossDoor(door);
        // Went first with people still on the other side: it waits for them here instead of going back.
        if (AlivePlayers().Any(p => IsAudience(p) && p.isInsideFactory != goingIn))
        {
            sideLockInside = goingIn;
            sideLockUntil = Time.time + 45f;
        }
        var behind = AlivePlayers().Where(p => IsAudience(p) && p.isInsideFactory != goingIn &&
                                                Vector3.Distance(p.transform.position, door.entrancePoint.position) < 40f).ToList();
        AddEvent($"You went {(goingIn ? "inside" : "outside")} through {DoorName(door)}" +
                 (behind.Count > 0
                     ? $". {string.Join(" and ", behind.Take(2).Select(p => p.playerUsername))} {(behind.Count == 1 ? "is" : "are")} still on the " +
                       "other side: wait for them by the door."
                     : "."), decideNow: true);
        finishAt = Time.time + 0.5f;
        resumeBehaviour = Behaviour.BlendIn;
    }

    private void GoToShipStep()
    {
        if (Finishing()) return;
        if (shipSpot == Vector3.zero) shipSpot = ShipSpot();
        bool inRoom = InShipRoom();
        if (!inRoom) shipRoomSince = -1f;
        else if (shipRoomSince < 0f) shipRoomSince = Time.time;
        // All the way in, off the doorway, like a player making room for the others.
        if (!shipArrived && (!inRoom || (FlatDistance(transform.position, shipSpot) > 1.2f && Time.time - shipRoomSince < 5f)))
        {
            wantSprint = !inRoom && (StartOfRound.Instance.shipIsLeaving ||
                                     (TimeOfDay.Instance != null && TimeOfDay.Instance.normalizedTimeOfDay > 0.9f));
            GoTo(shipSpot, inside: false);
            return;
        }
        Stop();
        if (shipArrived) return; // made it with nothing to drop: waits in the ship until it decides otherwise
        shipArrived = true;
        if (Inventory.Items.Any(x => x.item.itemProperties.isScrap))
        {
            // Players drop their loot as soon as they're in, so it counts. BeginTask keeps what it did before the ship.
            BeginTask(Behaviour.Drop, null, 20f);
            dropAll = true;
            dropScrapOnly = true;
            stepAt = Time.time + Random.Range(0.3f, 0.7f);
            AddEvent("You're back in the ship with your scrap: dropping it so it counts.");
            return;
        }
        taskDone = true;
        AddEvent("You're back in the ship.", decideNow: true);
    }

    // Somewhere on the ship's floor, so several of them don't stand in one spot.
    private static Vector3 ShipSpot()
    {
        var b = StartOfRound.Instance.shipInnerRoomBounds.bounds;
        for (int i = 0; i < 8; i++)
        {
            var p = new Vector3(Random.Range(b.min.x + 1f, b.max.x - 1f), b.min.y + 0.5f, Random.Range(b.min.z + 1f, b.max.z - 1f));
            if (NavMesh.SamplePosition(p, out NavMeshHit hit, 2f, NavMesh.AllAreas) &&
                b.Contains(new Vector3(hit.position.x, b.center.y, hit.position.z)))
            {
                return hit.position;
            }
        }
        return RoundManager.Instance.GetNavMeshPosition(b.center, default, 4f);
    }

    // ---- doors

    private readonly Dictionary<EntranceTeleport, Vector3> doorSpots = new();

    // Where to stand to use a door: the walkable spot in front of it, at its height.
    private Vector3 DoorSpot(EntranceTeleport door)
    {
        if (doorSpots.TryGetValue(door, out Vector3 spot)) return spot;
        spot = ExitSpot(door.entrancePoint.position);
        doorSpots[door] = spot;
        return spot;
    }

    private bool AtDoor(EntranceTeleport door)
    {
        Vector3 at = door.entrancePoint.position;
        float reach = Mathf.Clamp(FlatDistance(DoorSpot(door), at) + 0.6f, 1.6f, 3f);
        return FlatDistance(transform.position, at) < reach && Mathf.Abs(at.y - transform.position.y) < 2.2f;
    }

    // GoTo for a point on the other side: walks to the best door, a moment there like a player holding the button,
    // then through.
    private void WalkThroughDoorToward(Vector3 point)
    {
        if (Time.time < sideLockUntil && sideLockInside == !Masked.isOutside)
        {
            WaitByDoor(); // it went first on purpose: the others come to it
            return;
        }
        if (routeDoor == null || routeDoor.isEntranceToBuilding != Masked.isOutside || Vector3.Distance(point, routeFor) > 15f)
        {
            routeDoor = DoorToward(point);
            routeFor = point;
            routePauseAt = -1f;
        }
        var door = routeDoor;
        if (door == null)
        {
            Masked.SetDestinationToPosition(point);
            return;
        }
        if (!AtDoor(door))
        {
            routePauseAt = -1f;
            Masked.SetDestinationToPosition(DoorSpot(door));
            return;
        }
        Stop();
        if (routePauseAt < 0f) routePauseAt = Time.time + Random.Range(0.3f, 0.7f);
        if (Time.time < routePauseAt || Time.time - lastDoorUse < 2f) return;
        routePauseAt = -1f;
        routeDoor = null;
        CrossDoor(door);
    }

    // The door on this side whose other end is closest to where it's going: the main entrance, or a fire exit when that's
    // clearly shorter, like a player who knows the way.
    private EntranceTeleport? DoorToward(Vector3 point)
    {
        EntranceTeleport? best = null;
        float bestCost = float.MaxValue;
        foreach (var d in DoorsHere())
        {
            float cost = Vector3.Distance(transform.position, d.entrancePoint.position) +
                         Vector3.Distance(d.exitScript.entrancePoint.position, point) + (d.entranceId == 0 ? 0f : 10f);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = d;
            }
        }
        return best;
    }

    private void CrossDoor(EntranceTeleport door)
    {
        var exit = door.exitScript;
        if (exit == null && door.FindExitPoint()) exit = door.exitScript;
        if (exit == null || exit.entrancePoint == null || ModNetwork.Instance == null) return;
        lastDoorUse = Time.time;
        Vector3 pos = ExitSpot(exit.entrancePoint.position);
        ModNetwork.Instance.MimicEntrance(Masked, door, pos, exit.entrancePoint.eulerAngles.y);
    }

    // Where a player comes out: the exit point itself. The Masked needs a navmesh point there, and the game's wide
    // search could pick one on a level below (it used to end up under the ground at the main entrance), so look close
    // first and only take a point at about the same height.
    private static Vector3 ExitSpot(Vector3 p)
    {
        foreach (float r in new[] { 0.5f, 1.5f })
        {
            if (NavMesh.SamplePosition(p, out NavMeshHit hit, r, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - p.y) < 1.2f) return hit.position;
        }
        // Around it, nearest first.
        Vector3? best = null;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < 12; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, i * 30f, 0f) * Vector3.forward;
            foreach (float d in new[] { 1.5f, 3f })
            {
                if (!NavMesh.SamplePosition(p + dir * d, out NavMeshHit hit, 1f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - p.y) >= 1.2f) continue;
                float dist = Vector3.Distance(hit.position, p);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    best = hit.position;
                }
            }
        }
        if (best.HasValue) return best.Value;
        Plugin.Log.LogWarning($"No walkable spot at the height of the exit at {p}; using the game's nearest point");
        return RoundManager.Instance.GetNavMeshPosition(p);
    }

    // Went through first: hangs around just past the door until the others come through (or the wait runs out).
    private void WaitByDoor()
    {
        wantSprint = false;
        var door = DoorsHere().OrderBy(d => Vector3.Distance(d.entrancePoint.position, transform.position)).FirstOrDefault();
        if (door == null)
        {
            Stop();
            return;
        }
        Vector3 at = door.entrancePoint.position;
        // A few steps out of the doorway, where whoever comes through can see it.
        Vector3 spot = TryNavPoint(at + door.entrancePoint.forward * 3f, 2f, out Vector3 nav) ? nav : at;
        if (FlatDistance(transform.position, spot) > 1.2f)
        {
            Masked.SetDestinationToPosition(spot);
            return;
        }
        Stop();
        if (!LookTarget().HasValue && Random.value < 0.08f) LookAtPoint(at + Vector3.up * 1.5f, Random.Range(1f, 2.5f));
    }

    // ---- telling the LLM and the overlay what it's doing

    private string CurrentAction() => Current == Behaviour.Entrance
        ? taskDoor == null || taskDoor.isEntranceToBuilding ? "go_inside" : "go_outside"
        : ActionName(Current);

    private string Doing()
    {
        switch (Current)
        {
            case Behaviour.PickUp:
                return taskItem != null ? $"going to pick up the {taskItem.itemProperties.itemName}" : "";
            case Behaviour.Drop:
                string what = dropAll ? (dropScrapOnly ? "your scrap" : "everything")
                    : taskItem != null ? "the " + taskItem.itemProperties.itemName : "it";
                return $"dropping {what}" + (giveTo != null ? $" for {giveTo.playerUsername}" : "");
            case Behaviour.Entrance:
                return taskDoor != null ? $"going {(taskDoor.isEntranceToBuilding ? "inside" : "outside")} through {DoorName(taskDoor)}" : "";
            case Behaviour.GoToShip:
                return taskDone ? "waiting in the ship" : "heading back to the ship";
        }
        if (Time.time < sideLockUntil && sideLockInside == !Masked.isOutside) return "waiting by the door for the others to come through";
        return "";
    }

    // The task's item as the LLM would name it: its id when it's in view this time.
    private string TaskItemRef(Dictionary<string, GrabbableObject> view)
    {
        if (Current == Behaviour.Drop && dropAll) return dropScrapOnly ? "scrap" : "all";
        if (taskItem == null || Current is not (Behaviour.PickUp or Behaviour.Drop)) return "";
        foreach (var kv in view)
        {
            if (kv.Value == taskItem) return kv.Key;
        }
        return taskItem.itemProperties.itemName;
    }

    private string ItemsOverlay()
    {
        var inv = Disguise != null ? Disguise.Inventory : null;
        string doing = Doing();
        string carried = inv == null || inv.IsEmpty ? ""
            : string.Join(", ", inv.Items.Select(x => (x.slot == inv.Current ? "*" : "") + x.item.itemProperties.itemName +
                                                      (x.slot == MimicInventory.Belt ? " (belt)" : ""))) +
              $" ({MimicInventory.Pounds(inv.CarryWeight)} lb)";
        if (doing.Length == 0 && carried.Length == 0) return "";
        return doing + (doing.Length > 0 && carried.Length > 0 ? " | " : "") + carried + "\n";
    }
}
