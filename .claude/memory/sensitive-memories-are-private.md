---
name: sensitive-memories-are-private
description: Any memory holding personal or sensitive information must go in private/, never in the committed half
metadata:
  type: feedback
---

A memory that contains personal or sensitive information must be written to `private/` and indexed in `private/MEMORY.md`, never in the committed memory directory. This covers IP addresses, hostnames, SSH keys and other credentials or tokens, people's names, email addresses, physical addresses, phone numbers, and anything similar.

**Why:** The public memory directory is committed to the repository, so anything written there is published to everyone with access to it.

**How to apply:** Before saving a memory, check whether the fact carries any such detail; if it does, save the whole memory under `private/`. Do not split it so that a sanitised half stays public and do not reference the private memory's name from the public `MEMORY.md`, which would leak the detail through the index. If a fact is worth recording but the sensitive part is incidental, write the general rule publicly with the specific values in a private memory.
