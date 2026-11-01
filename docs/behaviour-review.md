# Mimic behaviour review

## The prompt, adapted to PlayerMimics

> You're improving a Lethal Company mod in which a Masked enemy disguises itself as one specific player in the lobby
> and has to pass as them until it strikes. Its body is driven by a host-side brain (Unity, per-frame motion and a
> 5 Hz AI tick); what it says and its big choices come from a local LLM that answers in JSON.
>
> **Goal:** friends who know each other well should not be able to tell the Masked from the player it copies by how it
> moves, reacts, decides and talks, until it's too late. Judge it on movement, reaction times, decisions and how it
> deals with the world (items, doors, monsters, hazards).
>
> **Requirements**
> 1. Learn from the round itself: what the copied player does (camera, crouching, jumping, sprinting, spacing,
>    talking) is the reference, not a generic player.
> 2. Adapt during the round: react to what players say and do to it (suspicion, accusations, running away).
> 3. Act right in each kind of situation: walking with the group, looting, monsters, hazards, being alone with
>    someone, being found out.
> 4. Stay unpredictable: no fixed timings, no perfect reactions, no tells that repeat.
> 5. It's a predator: it should take the openings a Masked would take, not wait to be caught.
>
> **Steps**
> 1. List what it records about players and what it does with it.
> 2. Find where its behaviour differs from a human player's, worst giveaway first.
> 3. For movement: walking, sprinting, evading, approaching.
> 4. For decisions: when the brain acts on its own and when the LLM decides, and whether the LLM gets what it needs.
> 5. Propose changes that fit the code (host only, no new client requirements unless needed), then build them.
>
> **Report:** design overview, gaps (ranked), what was changed, and how to check it in a game.
>
> **Examples:** someone splits off to loot a side room alone: the Masked should take that chance. A thumper comes
> round the corner: a player needs a moment to react before running.
>
> **Notes:** keep it fair to play against (it should be beatable by paying attention), and nothing friends see may
> reveal it before it strikes.

## The review (run against the code as of this change)

### Design overview

- **Body (host, per frame):** player walk speed and stamina, sprint build-up, crouch, body turning like a mouse look,
  glances at talkers and noises, a loose follow spot beside someone, hazard avoidance, and (new last change) replayed
  clips of its own player's camera, crouch, jump and sprint habits per situation.
- **Reflexes (host, AI tick):** fleeing monsters, being found out (hunt), grabbing scrap in passing, hazard stops.
- **Decisions (LLM, every few seconds or on events):** what to do and say, with permission gates from the brain
  (`can_attack`, talk policy, allowed actions).

### Gaps found, worst first

1. **Too passive.** Attacking needed `MinSecondsBeforeAttack` (90 s) near the crew *and* the LLM choosing it, and the
   LLM almost never picks attack on someone who isn't suspicious. Someone splitting off alone was safe.
2. **The attack itself gave it away.** It switched to an 8 m/s sprint straight at the target from any distance,
   staring at them. No player runs at a friend like that, and the cover counted as blown from the first step.
3. **Inhuman reaction time to monsters.** It started fleeing on the scan that first saw the monster: no startle,
   no moment to process.
4. **Generic group spacing.** The follow distance was a random 2.2 to 4 m for everyone; players have their own (some
   walk right on your back, some trail far behind).
5. **Prompt pushed against attacking.** The rule listed "someone alone with you" as only one of several conditions and
   ended with "Otherwise keep blending in", which the model reads as the default.
6. Later: it never looks back over its shoulder while fleeing; it doesn't feint (start an attack and back off when
   someone turns around); its talking rate doesn't follow its player's (how chatty they are).

### What was changed

| Gap | Change |
| --- | --- |
| 1 | **Ambush reflex.** When one player is alone with it (nobody else within 35 m of either, nobody with line of sight), after 6 s and `AloneAttackSeconds` (30 s) near the crew, it rolls `Aggression` (35% per 10 s; twice that when their back is turned, half when they're looking, ×1.3 inside). `can_attack` is also true in that case, so the LLM can pick it too. |
| 2 | **Stalk, then lunge.** An attack on someone who doesn't know starts at walking speed (it jogs when far), aiming for their back when they're turned away and looking around normally. It lunges when it's within 2.8 m, when they start running, or after 8 to 14 s. If anyone else shows up before the lunge, it drops back to following. The cover only counts as blown, and witnesses only count, from the lunge on. |
| 3 | **Reaction delay.** It reacts to a newly seen monster 0.2 to 0.55 s later (less when it's right on it). |
| 4 | **Learned spacing.** The host tracks how far each player keeps from the nearest other player while walking together; their Masked uses that (±15%) for its follow spot once about 10 s of walking has been seen. |
| 5 | **Prompt.** The rule now says it's a predator playing their friend: alone with someone and allowed to attack, going for them is usually right even if they don't suspect anything, and an attack starts as a casual walk-over. |

Gap 6 is left for later.

### How to check it in a game

- Turn on `ShowBrainOverlay`: it shows `alone with X Ns`, then `stalking (Ns)` and `lunging`.
- Walk off alone with a Masked for half a minute and turn your back: it should walk up and lunge within a few
  seconds. With someone else nearby it shouldn't.
- The log says `ambushes X` when the reflex fires and `attacks X (why)` at the lunge.
