#!/usr/bin/env bash
# tests/check.sh: generates the template with fixed values (all options off, then all on), fails if any placeholder
# or template conditional is left over, checks formatting, and builds in Release and Debug.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/template-check.XXXXXX")"
trap 'rm -rf "$work"' EXIT
short="$(jq -r '.shortName | if type == "array" then .[0] else . end' "$root/.template.config/template.json")"
dotnet new install "$root" --force --debug:custom-hive "$work/hive" >/dev/null

# JSON, XML and MSBuild would each mangle this text if it got through unescaped.
description=$'Checks the "template" & its <options> in C:\\mods; $(Version) 100%3B @(Compile)\tit\'s *?'
common=(--description "$description" --modAuthor "Template Check \$(Version)"
  --team TemplateCheck --idPrefix com.templatecheck --repositoryUrl 'https://example.com/check?a=1&b=2')
declare -A variants=(
  [off]=""
  [on]="--netcode true --assets true --config true --configSync true --categories items --categories misc"
  [client]="--audience client"
)
leftover='Mod(Template|Xml[A-Z]|Description|Author|Team|IdPrefix|Audience|Categories|RepositoryUrl|Year)|modtemplate|#if \(|#endif'
status=0
for name in "${!variants[@]}"; do
  out="$work/$name/CheckMod"
  # shellcheck disable=SC2086 # the variant's options split on spaces
  dotnet new "$short" --debug:custom-hive "$work/hive" -n CheckMod -o "$out" "${common[@]}" ${variants[$name]} >/dev/null
  # CHANGELOG.md is the new mod's own: it must not carry the template's release notes.
  if [ "$(cat "$out/CHANGELOG.md")" != "# Changelog" ]; then
    echo "$name: the generated CHANGELOG.md holds more than its heading" >&2
    status=1
  fi
  if grep -rInE "$leftover" "$out"; then
    echo "$name: the generated project keeps a placeholder or a template conditional (above)" >&2
    status=1
  fi
  (cd "$out" && git init -q && git add -A && git -c user.name=check -c user.email=check@example.com commit -qm check &&
    dotnet restore --locked-mode >/dev/null && dotnet format --verify-no-changes --no-restore &&
    dotnet build -c Release --no-restore >/dev/null &&
    dotnet build -c Debug --no-restore >/dev/null &&
    ! ls artifacts/bin/CheckMod/*/*_original.dll 2>/dev/null &&
    # A netcode mod's DLL must reference every game assembly at the version it compiled against: the patcher's own
    # Unity.Netcode.Runtime 4.0.0.0 reference makes Unity's API updater prompt. The target fails on any mismatch.
    { [ "$name" != on ] || dotnet msbuild src/CheckMod -p:Configuration=Release -nologo -v:q -t:ResolveAssemblyReferences\;VerifyNetcodeReferences; } &&
    jq -e --arg d "$description" '.description == $d' artifacts/bin/CheckMod/release/manifest.json >/dev/null) ||
    { echo "$name: the generated project is not formatted, does not build in Release and Debug, keeps an unpatched DLL, references a game assembly at another version than it compiled against, or writes a manifest.json without the description as given" >&2; status=1; }
done
# The version comes from the latest vX.Y.Z tag or a release tag CI passes in. A pre-release tag doesn't count.
out="$work/off/CheckMod"
(cd "$out" && git tag v1.2.3 && dotnet build -c Release --no-restore >/dev/null &&
  jq -e '.version_number == "1.2.3"' artifacts/bin/CheckMod/release/manifest.json >/dev/null &&
  dotnet build -c Release --no-restore -p:ReleaseTag=v2.0.0 >/dev/null &&
  jq -e '.version_number == "2.0.0"' artifacts/bin/CheckMod/release/manifest.json >/dev/null &&
  git tag v1.3.0-rc1 && dotnet build -c Release --no-restore >/dev/null &&
  jq -e '.version_number == "1.2.3"' artifacts/bin/CheckMod/release/manifest.json >/dev/null) ||
  { echo "version: a tag did not become the version as it should" >&2; status=1; }

# A git that warns on stderr (as under an AppImage's library path) must not leak the warning into the version.
mkdir -p "$work/noisy-git"
printf '#!/bin/sh\necho "git: loader warning" >&2\nexec %s "$@"\n' "$(command -v git)" >"$work/noisy-git/git"
chmod +x "$work/noisy-git/git"
(cd "$out" && PATH="$work/noisy-git:$PATH" dotnet build -c Release --no-restore -p:ReleaseTag= >/dev/null &&
  ! grep -rq "loader warning" artifacts/obj/CheckMod/release/CheckMod.AssemblyInfo.cs) ||
  { echo "version: git's stderr ended up in the version" >&2; status=1; }

# content/ ships as it is: Mortar writes each content tick there (mortar/<kind>/, moresuits/, a sound pack), and
# every shape has to reach the build output and the package under plugins/, and content/'s own README stays out.
(cd "$out" && mkdir -p content/mortar/tv content/moresuits content/sounds &&
  echo clip >content/mortar/tv/clip.webm && echo suit >content/moresuits/suit.png &&
  echo '{"name":"CheckMod"}' >content/sound_pack.json && echo sound >content/sounds/a.ogg &&
  dotnet build -c Release --no-restore >/dev/null &&
  for f in mortar/tv/clip.webm moresuits/suit.png sound_pack.json sounds/a.ogg; do
    [ -f "artifacts/bin/CheckMod/release/$f" ] || exit 1
  done &&
  zip="$(find artifacts -name '*.zip' -newer content/sounds/a.ogg | head -n 1)" && [ -n "$zip" ] &&
  unzip -Z1 "$zip" >"$work/zip.txt" &&
  for f in mortar/tv/clip.webm moresuits/suit.png sound_pack.json sounds/a.ogg; do
    grep -qx "plugins/$f" "$work/zip.txt" || exit 1
  done &&
  ! grep -q 'README.md$' <(grep '^plugins/' "$work/zip.txt")) ||
  { echo "content: content/ did not reach the build output and the package's plugins/ as it is" >&2; status=1; }

mkdir -p "$out/tests/CheckMod.Tests"
cat >"$out/tests/CheckMod.Tests/CheckMod.Tests.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/CheckMod/CheckMod.csproj" />
    <PackageReference Include="xunit.v3" Version="4.0.2" />
  </ItemGroup>
</Project>
XML
cat >"$out/tests/CheckMod.Tests/ConfigTests.cs" <<'CS'
using System.IO;
using BepInEx.Configuration;
using Xunit;

namespace CheckMod.Tests;

public class ConfigTests
{
    [Fact]
    public void BepInExLoads()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Assert.Equal(3, new ConfigFile(path, false).Bind("General", "Number", 3, "").Value);
    }
}
CS
(cd "$out" && dotnet sln CheckMod.slnx add tests/CheckMod.Tests/CheckMod.Tests.csproj &&
  dotnet build -c Release && dotnet test -c Release --no-build) >"$work/tests.log" 2>&1 ||
  { cat "$work/tests.log" >&2; echo "tests: a test project next to the plugin does not build or run" >&2; status=1; }

# Netcode means every player needs the mod, so any other audience has to fail.
dotnet new "$short" --debug:custom-hive "$work/hive" -n CheckMod -o "$work/refused/CheckMod" "${common[@]}" \
  --netcode true --audience host >/dev/null
if (cd "$work/refused/CheckMod" && dotnet build -c Release >/dev/null 2>&1); then
  echo "refused: netcode with audience host built" >&2
  status=1
fi
dotnet new "$short" --debug:custom-hive "$work/hive" -n CheckMod -o "$work/unshared/CheckMod" "${common[@]}" \
  --configSync true >/dev/null
if ! (cd "$work/unshared/CheckMod" && dotnet build -c Release 2>&1 || true) | grep -q "configSync needs config on"; then
  echo "refused: configSync without config built, or failed for another reason" >&2
  status=1
fi
exit "$status"
