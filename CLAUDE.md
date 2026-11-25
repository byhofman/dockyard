# Dockyard

- No workarounds for Microsoft Defender in code: not around Controlled Folder Access, ASR rules or SmartScreen, and no
  routing writes or launches through a process Defender trusts. Dockyard's builds are not signed and Defender is set
  differently per machine, so when Defender blocks something Dockyard says what was blocked and how the user allows it,
  and leaves the decision to them.
