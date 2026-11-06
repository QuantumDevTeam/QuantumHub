# Contributing to QuantumHub

- [README.md](README.md)

Thank you for your interest in contributing to **QuantumHub**.

QuantumHub is a long-term project developed with a strong focus on architecture, modularity,
and sustainability. Contributions are welcome, but contributors are expected to understand and
respect the core design principles described below.

---

## Core Philosophy

QuantumHub is a **kernel, not an application**.

The hub routes messages, loads plugins, stores configuration, and resolves dependencies. It
knows nothing about what any plugin does, and without plugins it does nothing at all.

This leads to a few rules:

* Features belong in plugins, not in the hub
* The hub never looks inside a message payload: its format belongs to the plugins
* The hub must not limit how a plugin works. A plugin decides what to do and how to do it
* In-process plugins and external plugins see the same API
* The protocol must stay usable from any language, not only from .NET

If a change makes the hub aware of one specific plugin, it is most likely a change to the wrong
place.

---

## Project Status

QuantumHub is currently in active development.

* APIs are unstable
* Internal architecture may change
* Breaking changes are expected

By contributing, you acknowledge that the project is evolving and that some work may need to
be revised or refactored in the future.

---

## Contribution Scope

Contributions may include, but are not limited to:

* The hub core and transports
* Plugins and client SDKs for other languages
* The GUI
* Tooling and infrastructure
* Documentation
* Prototypes and research implementations

Not all contributions are expected to be production-ready. Research-oriented or exploratory
work is acceptable when clearly marked and documented.

---

## Architectural Changes

Significant architectural changes must be discussed before implementation.

If your contribution:

* Changes the wire protocol or the shared memory layout
* Alters the plugin API in `QuantumHub.Contracts`
* Introduces new core concepts
* Redefines subsystem responsibilities

please open an issue or discussion describing the motivation, design, and trade-offs.

---

## Coding Guidelines

* Follow existing code style and conventions
* Prefer clarity over premature optimization
* Keep modules loosely coupled
* Avoid unnecessary dependencies between modules
* Document public APIs with XML comments: undocumented public members are build warnings
* Comments explain why, not what
* Cover behavior changes with tests

---

## Building and Testing

```bash
dotnet build QuantumHub.sln
```

```bash
dotnet test tests/QuantumHub.Tests
```

```bash
powershell -ExecutionPolicy Bypass -File scripts/docs.ps1
```

---

## Commit and Workflow Rules

* Keep commits focused and atomic
* Do not include version numbers in commit messages
* Use descriptive commit messages (e.g. `[Core] add plugin hot reload`)
* Versioning is handled exclusively via Git tags

Refer to [GitPolicy.md](docs/GitPolicy.md) and [Versioning.md](docs/Versioning.md) for detailed
workflow and versioning rules.

---

## Licensing and Contributions

By submitting contributions, you agree to the terms described in [CLA.md](CLA.md).

Contributions are provided "as is", without warranty of any kind, express or implied.

Do not submit code, assets, or content that you do not have the legal right to contribute.

---

## Final Notes

QuantumHub values thoughtful engineering, architectural discussion, and long-term thinking.

If you are unsure about a contribution, open a discussion first. Collaboration and
communication are preferred over silent assumptions.

### Welcome aboard.

---

Also see [README.md](README.md)
