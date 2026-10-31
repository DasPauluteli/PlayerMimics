# Asset bundles

Build the bundle in a Unity 2022.3.62 project (the game's Unity), name it `modtemplate.bundle`, and put it here. Every
`*.bundle` in this folder ships beside the plugin and lands in each build's output, so a mod manager's dev folder
picks it up. The plugin loads `modtemplate.bundle` in `Awake`.
