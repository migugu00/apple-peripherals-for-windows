# Apple Keyboard Filter

This is a HID lower-filter driver for Apple Magic Keyboard devices. It turns Apple's vendor-specific Globe/Fn bit into the standard HID F23 key before Windows converts the HID report into keyboard messages.

The user-mode Apple Peripherals bridge already maps F23/Fn/Globe through the normal settings file, so the filter deliberately emits F23 instead of Ctrl. That keeps the physical Control key free to be mapped to Windows while Globe/Fn can be mapped to Control.

The emitted usage is configurable with the `GlobeHidCode` service value, and the Lock/Touch ID key can emit a usage through `LockHidCode`. See [the driver guide](../../docs/keyboard-filter-driver.md#driver-settings).

The driver is based on the MIT-licensed WinAppleKey HID lower-filter approach by George Samartzidis, with the report transformation changed for this app's architecture. Do not ship unsigned builds to public users. Public installer builds must bundle an attestation-signed or HLK-signed driver package.
