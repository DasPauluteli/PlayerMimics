using System.Linq;
using UnityEngine;

namespace PlayerMimics;

// Host only. The crew shut the ship's doors with it outside (the classic way to keep a Masked out). A player would
// stand at the door and beg to be let in, more and more urgently once the ship is leaving, so it does too.
internal sealed partial class MimicBrain
{
    private static HangarShipDoor? shipDoor;
    private float shutOutSince = -1f;

    private static HangarShipDoor? ShipDoor() => shipDoor != null ? shipDoor : shipDoor = Object.FindObjectOfType<HangarShipDoor>();

    // From the AI tick.
    private void NoticeShutOut()
    {
        var round = StartOfRound.Instance;
        var door = ShipDoor();
        var point = door != null ? door.outsideDoorPoint : null;
        if (round == null || point == null || round.inShipPhase || !round.hangarDoorsClosed || !Masked.isOutside ||
            InShipRoom() || CoverBlown || Fleeing || Current == Behaviour.Attack || !WantsInShip(round) ||
            Vector3.Distance(transform.position, point.position) > 25f)
        {
            shutOutSince = -1f;
            return;
        }
        if (shutOutSince < 0f) shutOutSince = Time.time;
        shutOutDoor = point.position;
        // GoToShipStep takes it to the door and waits there while it's shut.
        if (Current != Behaviour.GoToShip) BeginTask(Behaviour.GoToShip, null, 150f);

        var inside = AlivePlayers().Where(p => p.isInHangarShipRoom && IsAudience(p)).Select(p => p.playerUsername).ToList();
        string who = inside.Count == 0 ? "nobody you can see" : string.Join(" and ", inside.Take(3));
        bool leaving = round.shipIsLeaving;
        AddEvent(leaving
                ? $"The ship is taking off and the doors are shut with you outside! {who} inside. Beg them to open up NOW."
                : $"The ship's doors are shut and you're outside, at the door. Inside: {who}. Ask them to open it and let you in.",
            "shutout", leaving ? 6f : 20f, decideNow: true);
        if (inside.Count > 0)
        {
            MustTalk(leaving
                ? "the ship is leaving without you: beg them to open the door, panicked, the way this player would"
                : "you're locked out of the ship: ask them to open the door, the way a player would (\"guys, open the door\")",
                leaving ? 6f : 10f);
        }
    }

    private Vector3 shutOutDoor;

    // From GoToShipStep: true while it's waiting at the shut door.
    private bool WaitAtShutDoor()
    {
        if (shutOutSince < 0f) return false;
        if (FlatDistance(transform.position, shutOutDoor) > 1.5f) GoTo(shutOutDoor, inside: false);
        else Stop();
        return true;
    }

    // It would be heading in anyway: going back to the ship, following someone who's in there, or the ship is leaving
    // with people aboard.
    private bool WantsInShip(StartOfRound round) =>
        Current == Behaviour.GoToShip || (Target != null && Target.isInHangarShipRoom) ||
        (round.shipIsLeaving && AlivePlayers().Any(p => p.isInHangarShipRoom));
}
