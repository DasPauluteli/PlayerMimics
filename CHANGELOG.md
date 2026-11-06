# Changelog

## 1.1.2

- Masked are animated the way the game animates other players on your screen, instead of with your own
  first-person body animations: no more "ninja run" arms, and items are held like players hold them
- Masked come out of a kill still walking and holding what they had

## 1.1.1

- Masked play back real things their player said on voice chat, and the AI picks when ("I'm real!", "over here").
  Without the AI server they play random clips now and then, like Mirage (`RandomClipsWithoutServer`,
  `RandomClipSeconds`). Clips about dying, the mimics or the mod are never kept, and dead players are never recorded
- TooManyEmotes support: Masked use the emotes their player has unlocked
- Masked notice more: sarcasm ("sure, you're totally real") and being sent the other way give them away too
- Hitting a Masked turns it on you (a stop sign still makes it run), and killed Masked fall over instead of standing
- Masked shut out of the ship wait at the door and ask to be let in, begging once the ship takes off
- More natural talk: no more "lmao" unless their player says it (abbreviations are spelled out for the voice), no
  "I'm good, that was close" from every Masked, and Masked don't repeat each other
- Faster answers with several Masked around: they skip thinking when nothing changed and let the one being talked to
  answer first
- Hosts can collect training data for a future AI for the mod (`CollectTrainingData`, off by default): decisions are
  saved as text on the host's PC, never audio, and the popup asks players again while it's on
- The voice popup looks like the game's pause menu, and you no longer turn while it's open
- More Coroner death messages
- Fixed: the right arm held up when holding nothing
- New icon

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
