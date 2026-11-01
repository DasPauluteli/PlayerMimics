# Changelog

## Unreleased

- Masked walk around landmines and spider webs they notice, and set off the ones they miss (`HazardMistakes`)
- Faster answers: the voice starts as soon as the line is written, talking to a Masked interrupts what it was thinking
  about, shorter prompts, fewer idle decisions, and answers aren't dropped as repeats so easily
- The AI server is now in the repo (`server/`), with setup and start scripts for Windows and Linux
- New description with the features, optional mods and how hosts set up the AI server

## 0.1.1

- fixed uncomitted files

## 0.1.0

- Give mimics a player inventory, door use and multi-mimic coordination
- Give the brain location and round context (ship/outside/inside, moon, time, quota)
- Add Coroner integration: name the disguise in the cause of death
- First PlayerMimics version: disguise, LLM brain, voice, consent, revive, debug tools
- Start PlayerMimics
- Initial commit

## 0.2.0

### Added

- A `content/` folder, copied as it is into the package (`content/mortar/tv/clip.mp4` ships as `plugins/<Author-Mod>/mortar/tv/clip.mp4`) and into the build output.
