# BepInEx 5 mod template for Lethal Company

A `dotnet new` template for a BepInEx 5 plugin: Thunderstore packaging, version from git tags, optional netcode,
settings shared with the lobby through ConfigSync, and debugger attach for VS Code and Rider.

The files are full of placeholders, so a repo made with "Use this template" won't build until `dotnet new` fills them in.
Make a mod with [Mortar](https://mortar.rethunk.tech)'s Develop tab, or from a clone of this repository:

```sh
dotnet new install .
dotnet new mortar-lc-bepinex5 -n MyMod --description "What it does." --modAuthor "You" --team YourTeam --idPrefix com.you
```

`dotnet new mortar-lc-bepinex5 -h` lists every option. The generated mod's DEVELOPING.md covers debugging and
publishing.
