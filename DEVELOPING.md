# Developing ModTemplate

## Commit hook

`lefthook.yml` runs gitleaks on staged files before each commit. Install [lefthook](https://lefthook.dev) and
[gitleaks](https://github.com/gitleaks/gitleaks), then run `lefthook install` once.

## Debugging

In the profile you test in, set `debug_enabled = true` in `doorstop_config.ini`, and `debug_suspend = true` if the game
should wait for the debugger at start-up. Start the game, then run Attach to Lethal Company in VS Code (Mono Debug
extension) or Rider (Mono Remote on 127.0.0.1:10000). Debug builds include symbols, so breakpoints work.

## Tests

Code that doesn't touch Unity can be tested outside the game. Put an xUnit v3 project under `tests/`, for example
`tests/ModTemplate.Tests/ModTemplate.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/ModTemplate/ModTemplate.csproj" />
    <PackageReference Include="xunit.v3" Version="4.0.2" />
  </ItemGroup>
</Project>
```

Add it to `ModTemplate.slnx` and run `dotnet test`. CI runs it after the build. The plugin's classes derive from Unity
types, so tests reach the code through other classes, with `InternalsVisibleTo` on the plugin project for internal ones.

## Publishing

Pushing a `vX.Y.Z` tag publishes from CI with the `THUNDERSTORE_API_TOKEN` secret, once the build passes. To publish from your own computer,
create a service account token for the team on Thunderstore (Settings, Teams, Service Accounts) and store it once.
Mortar's Develop tab stores it in the same place when you upload from there. The Flatpak build of Mortar does that only
when it can use your system keyring; otherwise it keeps the token inside its sandbox, where this build can't read it,
so store it yourself as below.

- Linux: `secret-tool store --label "Thunderstore ModTeam" service thunderstore username ModTeam`, then paste the token.
- Windows: `Install-Module CredentialManager`, then
  `New-StoredCredential -Persist LocalMachine -Target 'thunderstore:ModTeam' -Password <token>`.

Then run `dotnet build -c Release -p:PublishTS=true`. Don't add `-bl` or `-v:d` to a publish build: MSBuild logs task inputs, and the token is one.
