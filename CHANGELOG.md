# Changelog

## 1.0.0

- Masked copy their player's habits: how they look around, crouch-spam, jump and sprint in different situations
  (`CopyHabits`). Masked can jump now, and keep the same distance from others as their player does
- Masked are more dangerous: someone alone with one, with nobody else around, can get ambushed even if they don't
  suspect it (`Aggression`, `AloneAttackSeconds`). Attacks on someone who doesn't see it coming start as a casual
  walk-over and only turn into a lunge up close
- Masked know when they've been found out (called a mimic, then someone runs or says "stay away") and attack right
  away instead of playing along; running away counts as suspicious, and suspicion fades slower
- Masked pick up scrap they walk right past while they're with players (`GrabScrapNearPlayers`), Masked that spawn
  on their own start out holding a piece of scrap (`SpawnWithScrap`), and they can take the apparatus out of its
  machine when asked
- Masked walk around landmines and spider webs they notice, and set off the ones they miss (`HazardMistakes`)
- Masked react to monsters after a human moment instead of instantly
- Faster answers: the voice starts as soon as the line is written, talking to a Masked interrupts what it was thinking
  about, and fewer answers get dropped as repeats or cut off
- Fixed: a new Masked could laugh right after spawning; doors said "Near activity detected" because of a disguised
  Masked; Masked ran from a Masked that was still being set up
- The AI server is now in the repo (`server/`), with setup and start scripts for Windows and Linux
- A shorter description, a new icon

## 0.1.1

- fixed uncomitted files

## 0.1.0

- Give mimics a player inventory, door use and multi-mimic coordination
- Give the brain location and round context (ship/outside/inside, moon, time, quota)
- Add Coroner integration: name the disguise in the cause of death
- First PlayerMimics version: disguise, LLM brain, voice, consent, revive, debug tools
- Start PlayerMimics
- Initial commit
