# PlayerMimics

Masked become impostor crewmates. A Masked copies a player's suit, MoreCompany cosmetics, name tag, animations,
footsteps and (with consent) voice, and a local LLM decides what it does: blend in, follow, stare, wander, lure someone
away, emote, talk, carry loot, go through doors, flee, or attack when the moment is right.

Replaces Mirage, MirageRevive, LethalMimics and MaskFixes. Don't install it together with them.

- **Who needs it:** everyone in the lobby.
- **Optional:** BetterEmotes by KlutzyBubbles (more emotes, including the sign), MoreCompany (cosmetics) and Coroner (the end-of-round
  report names who the killer was pretending to be, for example "Was killed by a Masked disguised as ImTK.").
- **Host only:** the local AI server (`~/.local/share/LethalMimicsAI`, start it with `start.sh`). Without it, Masked
  still disguise themselves and use simple scripted behaviour, but they don't talk.

## What a Masked does

- Looks like the player it copies: no mask and no glowing eyes, not even when it attacks, player animations, a name
  tag, no radar spin, can't be scanned.
- Moves like a player: walks at player speed (`WalkSpeed`), sprints on player stamina and runs out of breath, crouches
  to sneak, and turns and looks around like someone with a mouse: it glances at whoever talks, at loud noises and at
  things nearby. It hangs around the group and checks spots instead of trailing one person in a line.
- Carries things like a player: four slots and a utility belt for one tool, one item in its hand and the rest out of
  sight in its pockets. It knows what it carries and what it can see lying around. Asked to, it picks things up,
  switches to an item before dropping it, hands things over by dropping them at someone's feet, and takes loot back to
  the ship, where the scrap counts for the quota (credited to the player it copies). Two-handed items fill both hands,
  weight slows it down and drains its stamina, and it drops everything when it dies.
- Uses the main entrance and fire exits like a player, whichever way is shorter. Told to go in or out first, it goes
  and waits by the door for the others.
- Blends in for a while. It only attacks after `MinSecondsBeforeAttack` near the crew, when its cover is about to blow
  (suspicion), or when it's attacked. If nobody sees the kill, its cover holds.
- Watches for giveaways: being seen next to the real player, people asking "are you real?", someone keeping their
  distance, a BetterEmotes sign held up at it. Hit it with a stop or yield sign and it runs.
- Reacts to monsters like a player: runs from thumpers, giants, nutcrackers and the like, backs away from coil-heads
  and brackens, sneaks past eyeless dogs, and calls them out. With `MonstersNoticeMimics`, roaming spiders, thumpers,
  slimes and giants go for a Masked that's near a player, so they don't suspiciously ignore it.
- Talks like a player on voice chat, in the copied player's style (swearing included): it answers when talked to,
  says something unprompted about every `SmallTalkSeconds`, and doesn't repeat itself.
- Players who die with nobody watching get back up as a Masked copying them (`Revive > ChancePercent`, 100% by
  default). If others are close but didn't see the death, it tells them it's fine. With `Revive > OnlyWhenUnobserved`
  off, deaths someone saw come back too, and the Masked knows who saw it.

Every Masked starts with an empty memory. The AI server keeps each player's voice, speaking style and nicknames while
it runs, so clones get better over the session. When a round starts or ends it drops what Masked said and the chat
from before, so nothing from last round ends up in new lines.

## Voice and consent

The first time a player joins a PlayerMimics lobby, a popup asks whether their voice may be used. It says their voice
chat goes to the host and is used for an AI copy of their voice in this game, and nothing about Masked, so the
surprise stays. Only if they allow it is their voice chat sent to the host, transcribed, and used for a cloned voice.
Nothing is saved to disk unless the host turns on `Debug > SaveAudioToDisk`, and then the popup says so (and asks
again). Change the answer in the config (`Voice > VoiceCloningConsent`) or with `Voice > ResetConsentKey`.

## Testing alone

Set these in `BepInEx/config/com.pauluteli.PlayerMimics.cfg`:

- When you're the only player, a Masked copying you treats you as its crewmate on its own. `SoloMode = true` forces
  that even with others around.
- `SpawnMaskedKey = F8`: spawns a Masked in front of you (`SpawnCopies` picks who it copies).
- `MimicName = Bob`: new Masked go by that name instead (name tag, the name they answer to, what the AI calls itself),
  so a copy of you plays a second player. It still looks and sounds like the player it copies.
- `ShowBrainOverlay = true`, `LogPrompts = true`: see what each Masked thinks (including what it carries) and what the
  LLM was asked.
- `ForceBehaviour`, `NoAttack`, `RevealMasked`: test one behaviour, stay alive, see where they are.

DevLaunch can run a host and a client copy on one PC (`--lan --host --tile 1/2` and `--lan --join 127.0.0.1 --tile 2/2`).

## AI server

Endpoints used: `/health`, `/ingest` (voice in), `/decide` (the brain), `/speak_stream` + `/speak_poll` (cloned line,
streamed), `/speak_line` (cloned line in one piece, with `Voice > StreamLines = false`), `/forget` (consent withdrawn),
`/round` (a round started or ended).

A streamed line starts playing once `/speak_poll` says `start_ok`: `StreamLeadSeconds` of audio are ready, plus enough
extra that slower-than-real-time generation still won't run out before the line ends. The server learns how fast each
voice talks to guess how long a line will be.

The LLM runs on a local Ollama. The default is Orion 26B-A4B v1.1 (`hf.co/bartowski/TheDrummer_Orion-26B-A4B-v1.1-GGUF:Q4_K_M`), an uncensored
Gemma 4 finetune that swears the way friends on voice chat do. To use another one: `./start.sh --llm-model <name>`, with
an Ollama model name (`gemma4:26b-a4b` for plain Gemma) or a Hugging Face GGUF as `hf.co/<user>/<repo>:<quant>`. A model
that isn't there yet is downloaded on the first start.
