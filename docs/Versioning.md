# QuantumHub — Versioning Policy

- [README.md](./../README.md)

## 1. General Concept

QuantumHub uses **intentional and minimalistic versioning**.

A version is **the name of a project state** that can be:

* referenced
* built
* demonstrated
* used as a stable point of comparison

Versions are **not equal to commits** and are **never created automatically**.

---

## 2. Where Versions Exist

A version exists **only as a Git tag**.

* Commits do not contain version numbers
* Branches do not contain version numbers
* Version = tag name

---

## 3. Current Project Stage

QuantumHub is stable since `v1.0.0` and follows Semantic Versioning:

```
vMAJOR.MINOR.PATCH
```

The public API covered by these rules is:

* `QuantumHub.Contracts`, `QuantumHub.Client` and `QuantumHub.Ui.Contracts`
* the wire protocol: `contracts/proto` and `Envelopes.ProtocolVersion`
* the plugin manifest
* the `hub.*` methods and topics listed in `PROTOCOL.md`
* the `quantumhub` command line and `quantumhub://` links

`QuantumHub.Core`, the GUI and the installer are internal even though their types are public. Plugins and their capabilities have their own versions.

A plugin says which hubs fit it in the `hub` field of its manifest.

---

## 4. When a New Version Is Created

A new version is created when:

* a **logically complete set of changes** has accumulated
* the current state can be described in a single sentence
* the state is meaningful as a reference point

A version is **not created**:

* for every commit
* on a timer
* "just because it has been a while"

---

## 5. Pre-release Versions

For unstable, testing, or transitional states, pre-release modifiers are used:

```
v0.2.0-alpha
v0.2.0-beta
v0.2.0-rc.1
```

A pre-release version:

* is considered a distinct version
* is not equivalent to the final version without a suffix

---

## 6. Backward Compatibility

* MAJOR — breaking changes
* MINOR — API extensions
* PATCH — bug fixes and internal improvements

Versions before `v1.0.0` made no compatibility promises.

---

Also see:
- [GitPolicy.md](GitPolicy.md)
- [CONTRIBUTING.md](./../CONTRIBUTING.md)
- [README.md](./../README.md)