Emock
====

Emotion + Mock

Mock animator controller for controlling the avatar's facial expressions.

## Note

Except for EmockMenuItem, it is not recommended for users to configure this plugin's components directly. Instead, use external tools to configure them automatically.

- [lilEmo](https://github.com/lilxyzw/lilEmo)

## Design

- Complex conditional branching and input operations that determine which animation to play are handled exclusively by the avatar owner. (`EmockController`)
- Once the animation to be played is determined, the index of that animation is sent to the network. (`EmockNetwork`)
- Receives the index sent by the owner and switches the animation. (`EmockNetwork`)
- The component plays a specific single animation based on the index. (`EmockAnimator`)
- Animation interpolation and fading are performed using Burst for each avatar, and the determined values ​​are applied to the avatar. (`EmockInterpolator`)

## Install

Please install [this plugin](https://github.com/lilxyzw/lilBasisPatcher) first. After that, running `Tools/lilBasisPatcher/Add Emock Components` makes the components available for use on the avatar.

This plugin is also available via the [VPM repository](https://github.com/lilxyzw/vpm-repos-basis).

## Make Emock compatible with Cilbox

### `com.basis.shim/BasisShims.asmdef`

```json
{
    ...
    "references": [
        ...
        "jp.lilxyzw.emock"
    ],
    ...
}
```

### `com.basis.shim/Shims/CilboxBasisCommon.cs`

```C#
		protected static readonly HashSet<string> commonWhiteListType = new HashSet<string>(){
			...

			// Add this
			"jp.lilxyzw.emock.EmockController",
			"jp.lilxyzw.emock.EmockNetwork",
		};

		protected static readonly Dictionary<Type, HashSet<string>> commonMethodWhitelist = new Dictionary<Type, HashSet<string>>()
		{
			...

			// Add this
			{ typeof(jp.lilxyzw.emock.EmockController), new HashSet<string>{ "SetParameter" } },
			{ typeof(jp.lilxyzw.emock.EmockNetwork),   new HashSet<string>{ "SetIndex" } },
		};
```

### `com.basis.shim/Shims/link.xml`

```xml
<linker>
    ...
	<assembly fullname="jp.lilxyzw.emock">
		<type fullname="jp.lilxyzw.emock.EmockController" preserve="all"/>
		<type fullname="jp.lilxyzw.emock.EmockNetwork" preserve="all"/>
	</assembly>
</linker>
```

## Client settings

Installing this plugin adds a new item to the settings.
