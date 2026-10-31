# PlayerMimics

Masked become impostor crewmates. A Masked copies a player's suit, MoreCompany cosmetics, name tag, animations,
footsteps and (with consent) voice, and a local LLM decides what it does: blend in, follow, stare, wander, lure someone
away, emote, talk, flee, or attack when the moment is right.

Replaces Mirage, MirageRevive, LethalMimics and MaskFixes. Don't install it together with them.

- **Who needs it:** everyone in the lobby.
- **Optional:** BetterEmotes (more emotes, including the sign) and MoreCompany (cosmetics).
- **Host only:** the local AI server (`~/.local/share/LethalMimicsAI`, start it with `start.sh`). Without it, Masked
  still disguise themselves and use simple scripted behaviour, but they don't talk.

## What a Masked does

- Looks like the player it copies: no mask, player animations and speeds, player footsteps, a name tag, no radar spin,
  can't be scanned.
- Blends in for a while. It only attacks after `MinSecondsBeforeAttack` near the crew, when its cover is about to blow
  (suspicion), or when it's attacked. When it attacks, its mask comes back (`RevealOnAttack`).
- Watches for giveaways: being seen next to the real player, people asking "are you real?", someone keeping their
  distance, a BetterEmotes sign held up at it. Hit it with a stop or yield sign and it runs.
- Players who die with nobody watching get back up as a Masked copying them.

## Voice and consent

The first time a player joins a PlayerMimics lobby, a popup asks whether their voice may be cloned. Only if they allow
it is their voice chat sent to the host, transcribed, and used for a cloned voice. Nothing is saved to disk unless the
host turns on `Debug > SaveAudioToDisk`, and then the popup says so (and asks again). Change the answer in the config
(`Voice > VoiceCloningConsent`) or with `Debug > ResetConsentKey`.

## Testing alone

Set these in `BepInEx/config/com.pauluteli.PlayerMimics.cfg`:

- `SoloMode = true`: Masked may copy you and talk to you.
- `SpawnMaskedKey = F8`: spawns a Masked in front of you (`SpawnCopies` picks who it copies).
- `ShowBrainOverlay = true`, `LogPrompts = true`: see what each Masked thinks and what the LLM was asked.
- `ForceBehaviour`, `NoAttack`, `RevealMasked`: test one behaviour, stay alive, see where they are.

DevLaunch can run a host and a client copy on one PC (`--lan --host --tile 1/2` and `--lan --join 127.0.0.1 --tile 2/2`).

## AI server endpoints used

`/health`, `/ingest` (voice in), `/decide` (the brain), `/speak_line` (cloned line), `/forget` (consent withdrawn).
