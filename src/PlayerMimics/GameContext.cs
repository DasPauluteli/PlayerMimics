using GameNetcodeStuff;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PlayerMimics;

// Plain-words descriptions of where things are and how the round is going, for the LLM.
internal static class GameContext
{
    private static Vector3? outsideDoor;
    private static Vector3? insideDoor;
    private static int doorsForRound = -1;

    private static void FindDoors()
    {
        // The search walks the scene, so do it once per landing.
        int round = StartOfRound.Instance.randomMapSeed;
        if (doorsForRound == round && outsideDoor.HasValue) return;
        doorsForRound = round;
        outsideDoor = RoundManager.FindMainEntrancePosition(getTeleportPosition: false, getOutsideEntrance: true);
        insideDoor = RoundManager.FindMainEntrancePosition(getTeleportPosition: false, getOutsideEntrance: false);
    }

    internal static string Interior()
    {
        return RoundManager.Instance == null ? "facility" : RoundManager.Instance.currentDungeonType switch
        {
            0 => "factory",
            1 => "mansion",
            4 => "mineshaft",
            _ => "facility",
        };
    }

    internal static string Where(PlayerControllerB p)
    {
        if (p.isInHangarShipRoom) return "inside the ship";
        return Where(p.transform.position, p.isInsideFactory);
    }

    internal static string Where(Vector3 pos, bool inside)
    {
        var round = StartOfRound.Instance;
        FindDoors();
        if (inside)
        {
            if (insideDoor.HasValue && Vector3.Distance(pos, insideDoor.Value) < 15f)
            {
                return $"inside the {Interior()}, near the main entrance";
            }
            return $"inside the {Interior()}";
        }
        if (round.shipInnerRoomBounds != null && round.shipInnerRoomBounds.bounds.Contains(pos)) return "inside the ship";
        float toShip = Vector3.Distance(pos, round.elevatorTransform.position);
        if (toShip < 18f) return "outside, right next to the ship";
        if (outsideDoor.HasValue && Vector3.Distance(pos, outsideDoor.Value) < 15f) return $"outside, at the {Interior()}'s main entrance";
        return $"outside on the moon, {Mathf.RoundToInt(toShip / 10f) * 10} m from the ship";
    }

    internal static string Clock(float hour)
    {
        int h = Mathf.FloorToInt(hour) % 24;
        string time = $"{(h % 12 == 0 ? 12 : h % 12)} {(h < 12 ? "AM" : "PM")}";
        string mood = h >= 21 || h < 5 ? "it's late night, outside is very dangerous and the ship leaves at midnight"
            : h >= 18 ? "it's getting dark, people are heading back soon"
            : h < 9 ? "the day just started"
            : "midday";
        return $"the time is {time} ({mood})";
    }

    internal static JObject Round()
    {
        var round = StartOfRound.Instance;
        var level = round.currentLevel;
        var time = TimeOfDay.Instance;
        var o = new JObject
        {
            ["moon"] = level != null ? level.PlanetName : "unknown",
            ["weather"] = level != null ? level.currentWeather.ToString().Replace("None", "clear") : "clear",
            ["interior"] = Interior(),
        };
        if (time != null)
        {
            o["quota"] = $"{time.quotaFulfilled} of {time.profitQuota} credits sold, {time.daysUntilDeadline} days left";
        }
        return o;
    }
}
