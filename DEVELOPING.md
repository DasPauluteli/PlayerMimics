# Developing PlayerMimics

## Commit hook

`lefthook.yml` runs gitleaks on staged files before each commit. Install [lefthook](https://lefthook.dev) and
[gitleaks](https://github.com/gitleaks/gitleaks), then run `lefthook install` once.

## Debugging

In the profile you test in, set `debug_enabled = true` in `doorstop_config.ini`, and `debug_suspend = true` if the game
should wait for the debugger at start-up. Start the game, then run Attach to Lethal Company in VS Code (Mono Debug
extension) or Rider (Mono Remote on 127.0.0.1:10000). Debug builds include symbols, so breakpoints work.

## Tests

Code that doesn't touch Unity can be tested outside the game. Put an xUnit v3 project under `tests/`, for example
`tests/PlayerMimics.Tests/PlayerMimics.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/PlayerMimics/PlayerMimics.csproj" />
    <PackageReference Include="xunit.v3" Version="4.0.2" />
  </ItemGroup>
</Project>
```

Add it to `PlayerMimics.slnx` and run `dotnet test`. CI runs it after the build. The plugin's classes derive from Unity
types, so tests reach the code through other classes, with `InternalsVisibleTo` on the plugin project for internal ones.

## Publishing

Pushing a `vX.Y.Z` tag publishes from CI with the `THUNDERSTORE_API_TOKEN` secret, once the build passes. To publish from your own computer,
create a service account token for the team on Thunderstore (Settings, Teams, Service Accounts) and store it once.
Mortar's Develop tab stores it in the same place when you upload from there. The Flatpak build of Mortar does that only
when it can use your system keyring; otherwise it keeps the token inside its sandbox, where this build can't read it,
so store it yourself as below.

- Linux: `secret-tool store --label "Thunderstore Pauluteli" service thunderstore username Pauluteli`, then paste the token.
- Windows: `Install-Module CredentialManager`, then
  `New-StoredCredential -Persist LocalMachine -Target 'thunderstore:Pauluteli' -Password <token>`.

Then run `dotnet build -c Release -p:PublishTS=true`. Don't add `-bl` or `-v:d` to a publish build: MSBuild logs task inputs, and the token is one.

## AI server protocol

Endpoints used: `/health`, `/ingest` (voice in), `/decide` (the brain), `/speak_stream` + `/speak_poll` (cloned line,
streamed), `/speak_line` (cloned line in one piece, with `Voice > StreamLines = false`), `/forget` (consent withdrawn),
`/round` (a round started or ended).

A streamed line starts playing once `/speak_poll` says `start_ok`: `StreamLeadSeconds` of audio are ready, plus enough
extra that slower-than-real-time generation still won't run out before the line ends. The server learns how fast each
voice talks to guess how long a line will be.

To answer faster, `/decide` streams the model's answer with `say` written first and starts the cloned line as soon as
that's done, while the rest is still being written (the mod then only plays it, or drops it with `/speak_cancel`). When
someone talks to a Masked that's still thinking about something else, a new request replaces the old one and the server
stops the old one. With nothing new going on, it rethinks about 2.5× less often.

The LLM runs on a local Ollama. The default is Orion 26B-A4B v1.1 (`hf.co/bartowski/TheDrummer_Orion-26B-A4B-v1.1-GGUF:Q4_K_M`), an uncensored
Gemma 4 finetune that swears the way friends on voice chat do. To use another one: `./start.sh --llm-model <name>`, with
an Ollama model name (`gemma4:26b-a4b` for plain Gemma) or a Hugging Face GGUF as `hf.co/<user>/<repo>:<quant>`. A model
that isn't there yet is downloaded on the first start.
