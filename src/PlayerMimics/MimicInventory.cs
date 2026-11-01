using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using UnityEngine;

namespace PlayerMimics;

// Runs on every client: a player's hotbar for a disguised Masked. Four slots plus the utility belt, one item in its
// right hand and the rest in its pockets, like a player's: pocketed items are hidden, weight slows it down, and scrap
// counts once it's in the ship. The host decides what to grab, switch to and drop (MimicItems.cs) and sends each step
// through ModNetwork, so every screen moves the items the same way.
internal sealed class MimicInventory : MonoBehaviour
{
    internal const int Belt = 4; // the utility belt (the game's slot 50): one-handed tools only, never scrap
    internal readonly GrabbableObject?[] Slots = new GrabbableObject?[5];
    internal int Current;
    internal float LastChange = -100f; // last grab, switch or drop: players can't do these back to back

    private MimicDisguise disguise = null!;
    private PlayerControllerB player = null!;
    private Transform holder = null!;
    private bool outside;
    private bool inShipRoom;
    private bool inElevator;
    private readonly Dictionary<GrabbableObject, bool> enemiesCouldGrab = new();
    private readonly Dictionary<GrabbableObject, List<Light>> litBeforePocket = new();

    // Unity's null: a destroyed item counts as no item.
    internal static GrabbableObject? Live(GrabbableObject? item) => item != null ? item : null;

    internal GrabbableObject? Held => Live(Slots[Current]);

    // Two-handed items fill both hands: no grabbing or switching until it's dropped, like a player.
    internal bool HandsFull => Held != null && Held!.itemProperties.twoHanded;

    // Everything it carries, with the slot it's in.
    internal List<(int slot, GrabbableObject item)> Items
    {
        get
        {
            var list = new List<(int, GrabbableObject)>();
            for (int i = 0; i < Slots.Length; i++)
            {
                var item = Live(Slots[i]);
                if (item != null) list.Add((i, item));
            }
            return list;
        }
    }

    internal bool IsEmpty => Items.Count == 0;

    // The game's carry weight: 1 plus what the items add, which divides walking speed and multiplies stamina use.
    internal float CarryWeight => Mathf.Clamp(1f + Items.Sum(x => x.item.itemProperties.weight - 1f), 1f, 10f);

    // What the HUD shows for a weight.
    internal static int Pounds(float weight) => Mathf.RoundToInt(Mathf.Clamp(weight - 1f, 0f, 100f) * 105f);

    internal int SlotOf(GrabbableObject item) => System.Array.IndexOf(Slots, item);

    // The slot a player's grab would use: the belt for tools when it's free, else the current slot, else the first empty.
    internal int FreeSlotFor(GrabbableObject item)
    {
        var p = item.itemProperties;
        if (Live(Slots[Belt]) == null && !p.isScrap && !p.twoHanded && !p.disallowUtilitySlot) return Belt;
        if (Current != Belt && Live(Slots[Current]) == null) return Current;
        for (int i = 0; i < Belt; i++)
        {
            if (Live(Slots[i]) == null) return i;
        }
        return -1;
    }

    internal bool CanTake(GrabbableObject item, out string why)
    {
        why = "";
        if (HandsFull)
        {
            why = $"your hands are full with the {Held!.itemProperties.itemName}";
            return false;
        }
        if (FreeSlotFor(item) < 0)
        {
            why = "your inventory is full";
            return false;
        }
        return true;
    }

    internal void Init(MimicDisguise d)
    {
        disguise = d;
        player = d.Player;
        outside = d.Masked.isOutside;
        if (holder == null) holder = MakeHolder(d.Player, d.Masked);
    }

    // Where other players see someone's held item: their serverItemHolder, under the right hand. The Masked has the
    // same rig, so put a copy of the holder on the same bone.
    private static Transform MakeHolder(PlayerControllerB player, MaskedPlayerEnemy masked)
    {
        var holder = new GameObject("PlayerMimicsItemHolder").transform;
        Transform rig = masked.creatureAnimator != null ? masked.creatureAnimator.transform : masked.transform;
        Transform? source = player.serverItemHolder;
        Transform? bone = null;
        if (source != null && source.parent != null)
        {
            var parts = new List<string>();
            for (Transform? t = source.parent; t != null && t != player.transform; t = t.parent) parts.Insert(0, t.name);
            int spine = parts.IndexOf("spine");
            if (spine >= 0) bone = rig.Find(string.Join("/", parts.Skip(spine)));
        }
        if (bone != null)
        {
            holder.SetParent(bone, false);
            holder.localPosition = source!.localPosition;
            holder.localRotation = source.localRotation;
            holder.localScale = source.localScale;
            return holder;
        }
        // A rig that doesn't match the player's: the right hand by name, else about where a hand would be.
        var hand = masked.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "hand.R");
        holder.SetParent(hand != null ? hand : masked.transform, false);
        if (hand == null) holder.localPosition = new Vector3(0.3f, 1.1f, 0.4f);
        Plugin.Log.LogWarning($"Masked #{masked.thisEnemyIndex}: no player item holder bone; held items use {(hand != null ? "hand.R" : "a fixed spot")}");
        return holder;
    }

    // ---- steps from the host (ModNetwork), the same on every client

    internal void Grab(GrabbableObject item, int slot)
    {
        if (item == null || slot < 0 || slot >= Slots.Length) return;
        if (item.isHeld) return; // a player got there first
        int had = SlotOf(item);
        if (had >= 0) Slots[had] = null;
        var old = Held;
        if (old != null && old != item) Pocket(old);
        Slots[slot] = item;
        Current = slot;
        LastChange = Time.time;

        if (!enemiesCouldGrab.ContainsKey(item)) enemiesCouldGrab[item] = item.grabbableToEnemies;
        item.grabbableToEnemies = false; // or a hoarder bug would pull it out of its hand
        item.parentObject = holder;
        item.hasHitGround = false;
        item.isPocketed = false;
        item.isInFactory = !disguise.Masked.isOutside;
        // Like a player's grab: off whatever furniture it sat on, back into the normal containers.
        var round = StartOfRound.Instance;
        if (item.transform.parent != round.propsContainer && item.transform.parent != round.elevatorTransform)
        {
            item.transform.SetParent(item.isInElevator ? round.elevatorTransform : round.propsContainer, true);
        }
        PullApparatus(item);
        item.GrabItemFromEnemy(disguise.Masked);
        item.isHeldByEnemy = true;
        item.EnablePhysics(false);
        item.EnableItemMeshes(true);
        ShowLights(item);
        if (disguise.Masked.IsServer) item.heldByPlayerOnServer = true; // turns down any player's grab that arrives late
        TrackShip(force: true);

        var sfx = disguise.Masked.creatureSFX;
        var grabSounds = round.playerGrabSFX;
        if (sfx != null && grabSounds != null && grabSounds.Length > 0) sfx.PlayOneShot(grabSounds[Random.Range(0, grabSounds.Length)]);
        if (sfx != null && item.itemProperties.grabSFX != null) sfx.PlayOneShot(item.itemProperties.grabSFX, 1f);
        disguise.OnGrab(item);
    }

    // What the apparatus does when a player grabs it (LungProp.EquipItem), which an enemy grab skips: out of the
    // machine with sparks, the lights flicker and the power goes off.
    private static void PullApparatus(GrabbableObject item)
    {
        if (item is not LungProp lung) return;
        if (lung.isLungDocked)
        {
            lung.isLungDocked = false;
            if (lung.disconnectAnimation != null) lung.StopCoroutine(lung.disconnectAnimation);
            lung.disconnectAnimation = lung.StartCoroutine(lung.DisconnectFromMachinery());
        }
        if (lung.isLungDockedInElevator)
        {
            lung.isLungDockedInElevator = false;
            var audio = lung.GetComponent<AudioSource>();
            if (audio != null && lung.disconnectSFX != null) audio.PlayOneShot(lung.disconnectSFX);
        }
    }

    internal void Switch(int slot)
    {
        if (slot < 0 || slot >= Slots.Length || slot == Current) return;
        var old = Held;
        if (old != null) Pocket(old);
        Current = slot;
        LastChange = Time.time;
        var now = Held;
        if (now != null)
        {
            now.isPocketed = false;
            now.EnableItemMeshes(true);
            ShowLights(now);
        }
        disguise.OnSwitch(old, now);
    }

    // target is local to the elevator (inElevator) or the props container, like a player's drop.
    internal void Drop(GrabbableObject item, Vector3 target, bool droppedInElevator, bool droppedInShipRoom, int floorYRot)
    {
        if (item == null) return;
        int slot = SlotOf(item);
        if (slot < 0) return;
        bool wasInHand = slot == Current;
        Slots[slot] = null;
        LastChange = Time.time;
        var round = StartOfRound.Instance;
        Release(item);
        item.transform.SetParent(droppedInElevator ? round.elevatorTransform : round.propsContainer, true);
        item.transform.localScale = item.originalScale;
        item.fallTime = 0f;
        item.startFallingPosition = item.transform.parent.InverseTransformPoint(item.transform.position);
        item.targetFloorPosition = target;
        item.floorYRot = floorYRot;
        item.isInFactory = !outside;
        // The copied player gets the credit, as if they'd carried it.
        player.SetItemInElevator(droppedInShipRoom, droppedInElevator, item);
        if (droppedInShipRoom && !round.inShipPhase) RoundManager.Instance.CollectNewScrapForThisRound(item);
        if (wasInHand && disguise != null) disguise.OnDrop(item);
    }

    // ---- local bookkeeping

    private void Pocket(GrabbableObject item)
    {
        item.isPocketed = true;
        item.EnableItemMeshes(false);
        HideLights(item);
        var audio = item.GetComponent<AudioSource>();
        if (audio != null && item.itemProperties.pocketSFX != null) audio.PlayOneShot(item.itemProperties.pocketSFX, 1f);
    }

    // A lit flashlight in a pocket would glow out of thin air.
    private void HideLights(GrabbableObject item)
    {
        var lit = item.GetComponentsInChildren<Light>().Where(l => l.enabled).ToList();
        if (lit.Count == 0) return;
        foreach (var light in lit) light.enabled = false;
        litBeforePocket[item] = lit;
    }

    private void ShowLights(GrabbableObject item)
    {
        if (!litBeforePocket.TryGetValue(item, out var lit)) return;
        litBeforePocket.Remove(item);
        foreach (var light in lit)
        {
            if (light != null) light.enabled = true;
        }
    }

    private void Release(GrabbableObject item)
    {
        item.parentObject = null;
        item.isHeldByEnemy = false;
        item.isPocketed = false;
        item.heldByPlayerOnServer = false;
        if (enemiesCouldGrab.TryGetValue(item, out bool could))
        {
            item.grabbableToEnemies = could;
            enemiesCouldGrab.Remove(item);
        }
        ShowLights(item);
        item.EnablePhysics(true);
        item.EnableItemMeshes(true);
        if (item is not ExtensionLadderItem) item.DiscardItemFromEnemy(); // the ladder's would unfold it
    }

    // Walking in or out of the ship with items, like a player: scrap carried in counts right away.
    private void TrackShip(bool force)
    {
        var round = StartOfRound.Instance;
        if (round == null || round.shipBounds == null || round.shipInnerRoomBounds == null) return;
        Vector3 pos = transform.position + Vector3.up * 0.3f;
        bool room = round.shipInnerRoomBounds.bounds.Contains(pos);
        bool elevator = round.shipBounds.bounds.Contains(pos);
        if (!force && room == inShipRoom && elevator == inElevator) return;
        inShipRoom = room;
        inElevator = elevator;
        foreach (var (_, item) in Items) player.SetItemInElevator(room, elevator, item);
    }

    private void Update()
    {
        if (disguise == null || disguise.Masked == null) return;
        outside = disguise.Masked.isOutside;
        // Gone from its hand some other way (despawned, taken): off the hotbar.
        for (int i = 0; i < Slots.Length; i++)
        {
            var item = Slots[i];
            if (ReferenceEquals(item, null)) continue;
            if (item != null && !item.isHeld && !item.deactivated && item.parentObject == holder) continue;
            Slots[i] = null;
            if (item != null)
            {
                if (enemiesCouldGrab.TryGetValue(item, out bool could)) item.grabbableToEnemies = could;
                enemiesCouldGrab.Remove(item);
                ShowLights(item);
                if (item.parentObject == holder) item.parentObject = null;
            }
            if (i == Current) disguise.OnDrop(item);
        }
        if (!IsEmpty) TrackShip(force: false);
    }

    // Despawned (the ship left, the round ended): let go of everything right here, on every client.
    private void OnDestroy()
    {
        var round = StartOfRound.Instance;
        if (round == null || round.propsContainer == null || round.elevatorTransform == null) return;
        foreach (var (_, item) in Items)
        {
            Vector3 floor = item.GetItemFloorPosition();
            bool elevator = round.shipBounds != null && round.shipBounds.bounds.Contains(floor);
            bool room = elevator && round.shipInnerRoomBounds != null && round.shipInnerRoomBounds.bounds.Contains(floor + Vector3.up * 0.3f);
            Drop(item, (elevator ? round.elevatorTransform : round.propsContainer).InverseTransformPoint(floor), elevator, room,
                (int)transform.eulerAngles.y);
        }
    }
}
