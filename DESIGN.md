# PlayerMimics: design

A full Masked overhaul. A Masked becomes an impostor crewmate: it looks, sounds, moves and acts like a real
player, and a local LLM decides what it does. Replaces Mirage, MirageRevive, LethalMimics and MaskFixes, so those
must not be installed together with it.

Everyone in the lobby needs the mod (visuals, footsteps, name tags, voice capture and the consent popup all run on
every client). The AI server only runs on the host.

## Parts

### 1. Disguise (every client)

When a Masked spawns, the host picks a player to copy and tells every client. Each client then makes it look like that
player:

- Suit: the game's own `MaskedPlayerEnemy.SetSuit`, plus any More Suits material.
- MoreCompany cosmetics: copy the target's cosmetic ids onto the Masked's rig.
- No mask: hide the `HeadMask*` objects and the mask's light and eyes.
- Name tag: a copy of the player's name tag canvas showing the copied name, with the same distance and fade rules.
- Radar: a normal player dot that doesn't spin, with the copied name on the ship monitor.
- Animations: the player's walking, crouching and sprinting animations instead of the zombie walk; no arms-out.
- Movement: player walk and sprint speeds, acceleration and turning, crouching, jumping over gaps; no instant 180s.
- Footsteps: the player footstep sounds for the surface under it, at a player's volume and range.
- Held items: may carry a scrap item or flashlight like a player would.

### 2. Brain (host only)

Each Masked runs a small set of behaviours. The LLM picks one, with a target, every few seconds and right away on
events.

| Behaviour | What it does |
|---|---|
| `blend_in` | Walks with the group at a normal distance, looks at things, picks up scrap, acts like a crewmate |
| `follow` | Follows one player |
| `stare` | Stops and looks at one player |
| `wander` | Explores alone, like someone looting |
| `lure` | Walks somewhere quiet and calls a player over |
| `attack` | Chases and grabs one player (the vanilla kill animation) |
| `flee` | Runs away from one player and breaks line of sight |
| `emote` | Plays a BetterEmotes emote (dance, point, wave, sign, ...) |
| `say` | Speaks a line in the copied player's cloned voice (combined with any of the above) |

**Events** that trigger a decision right away: being hit, a player pointing a weapon or flashlight at it, someone
talking near it or saying its name, someone holding up a sign (BetterEmotes sign), a player dying nearby, the real
player it copies coming into view of someone it's near, lights going out, the ship leaving.

**Context the LLM gets**, as compact text:

- Who it copies, whether that player is alive or dead, and where they are compared to the group.
- Nearby players: distance, whether they're looking at it, what they hold, their health, and recent things they said.
- How suspicious each player seems (rises when they ask "are you real?", keep their distance, aim at it or hit it).
- Recent events and what it did itself.
- Giveaways it must avoid: talking about being dead, appearing where the real player can't be, being seen next to
  the real player, coming back after dying.

**Rules the code enforces**, whatever the LLM says:

- No `attack` until it has spent a minimum time with the group (default 90 s). Exceptions: its cover is blown
  (suspicion above a threshold), it is attacked, or it is cornered alone with one player.
- Switch target when someone angers it (hits it, insults it, blocks it). Flee when shown a sign while it is still in
  disguise, or when badly hurt.
- At most N decisions per second across all Masked, and a fallback behaviour when the LLM is slow or offline.

**Output:** JSON with a strict schema, for example
`{"action":"follow","target":"ImTK","say":"wait up, I found a door","emote":null}`.
Ollama's JSON mode validates it, and anything invalid falls back to `blend_in`.

### 3. Voice (every client sends, host clones)

This reuses the existing local AI server (`~/.local/share/LethalMimicsAI`), extended with a `/decide` endpoint for the
brain. Its TTS, speech-to-text and voice building already work.

- Each client that consented sends its own voice chat to the host, cut into utterances. This is only what other
  players can already hear.
- The host transcribes it (for context and the voice sample) and builds a cloned voice per player.
- A Masked's lines are synthesized on the host and streamed to every client, then played from the Masked with
  the same filters as player voice chat (walkie-talkie, distance, muffling).

**Storage:** none. Voice samples live in memory and vanish when the server stops. The `SaveAudioToDisk` debug
setting writes them to disk. When the host has it on, the consent popup says so.

### 4. Revive (host only)

When a player dies and no living player had them in view (line of sight within about 30 m, checked over the last few
seconds), their body turns into a Masked copying them, after a short delay. Configurable chance and delay.

### 5. Consent (every client)

The first time a player joins a lobby with PlayerMimic, they get a popup:

- What happens: their voice chat is sent to the host, transcribed and used to clone their voice for Masked lines.
- That nothing is stored, or, when the host has `SaveAudioToDisk` on, that it is stored on the host's computer.
- **Allow** and **Don't allow** buttons, and how to change the choice later (the config file, or a key).

The choice is saved on their own computer. Without consent, their voice is never sent and nobody gets a cloned
voice of them. Masked copying them stay silent, or only use emotes. The host also refuses voice from anyone who
didn't send consent.

### 6. Debug settings (`[Debug]` section)

| Setting | Use |
|---|---|
| `SoloMode` | Lets a Masked copy you and talk to you when you're alone |
| `SpawnMaskedKey` | Spawns a Masked copying a chosen player in front of you |
| `ShowBrainOverlay` | Text above each Masked: its behaviour, target, suspicion and last LLM reply |
| `LogPrompts` | Writes every prompt and LLM reply to the BepInEx log |
| `ForceBehaviour` | Locks every Masked to one behaviour, to test it |
| `NoAttack` | Masked never attack |
| `RevealMasked` | Shows a red outline on Masked, so you can watch them |
| `SaveAudioToDisk` | Keeps voice samples on disk (listed in the consent popup) |
| `ResetConsentKey` | Clears your saved consent choice and shows the popup again |

## Build

- Mortar BepInEx 5 template (net standard 2.1, Netcode patcher, publicized game assembly).
- Project in `~/Documents/PlayerMimics`, built with `dotnet build`, linked into the Mortar "Development pack" profile.
- Soft dependencies: MoreCompany (cosmetics) and BetterEmotes (emotes); the mod still works without them.

## Milestones

1. Disguise: no mask, player animations and movement, footsteps, name tag, radar dot, suit and cosmetics.
2. Brain with simple scripted behaviours, without the LLM (follow, stare, wander, attack, flee).
3. LLM decisions through `/decide`, with context, suspicion and the hard rules.
4. Voice: consent popup, capture, cloned lines streamed to everyone.
5. Emotes, revive and the remaining debug tools.
