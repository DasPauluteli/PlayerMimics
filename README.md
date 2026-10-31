# ModTemplate

ModDescription

<!--#if (audience == "host") -->
- Who needs it: only the host.
<!--#elseif (audience == "client") -->
- Who needs it: only the player who installs it.
<!--#else -->
- Who needs it: everyone in the lobby.
<!--#endif -->
<!--#if (configSync) -->
- Config: `Enabled` turns the mod on or off. `Strength` uses the host's value for everyone in the lobby
  ([ConfigSync](https://thunderstore.io/c/lethal-company/p/Mortar/ConfigSync/)).
<!--#elseif (config) -->
- Config: `Enabled` turns the mod on or off.
<!--#else -->
- Config: none.
<!--#endif -->
