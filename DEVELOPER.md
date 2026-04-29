# Duet Software Framework Developer Setup Guide

This guide explains how to set up a development environment for [Duet Software Framework (DSF)](https://github.com/Duet3D/DuetSoftwareFramework) from scratch using [Visual Studio Code](https://code.visualstudio.com/) and [Docker](https://www.docker.com/).

No prior knowledge of VS Code, Docker, or DSF is assumed.

## Table of Contents

1. [Overview](#overview)
2. [Prerequisites](#prerequisites)
3. [Install Required Software](#install-required-software)
4. [Clone the Repository](#clone-the-repository)
5. [Open the Project in VS Code](#open-the-project-in-vs-code)
6. [Open in Dev Container](#open-in-dev-container)
7. [First Build](#first-build)
8. [Build Using VS Code Tasks](#build-using-vs-code-tasks)
9. [Deploy to a Remote DuetPi/SBC](#deploy-to-a-remote-duetpisbc)
10. [Optional: Build with Make](#optional-build-with-make)
11. [Working with Git in the Dev Container](#working-with-git-in-the-dev-container)
12. [Troubleshooting](#troubleshooting)

## Overview

Duet Software Framework is a collection of .NET services and tools that run on a Linux single-board computer (SBC) and communicate with a Duet board. For a higher-level project overview, see [README.md](README.md).

This repository includes a Dev Container configuration in [.devcontainer/devcontainer.json](.devcontainer/devcontainer.json) and [.devcontainer/Dockerfile](.devcontainer/Dockerfile). A Dev Container is a Docker-based development environment defined in code. When you open this project in VS Code, it can automatically build and start a container with all required build tools preinstalled.

Inside the Dev Container, you get:

- .NET SDK
- Git
- Make and packaging utilities
- SSH and rsync tools for deployment
- Node/npm tools needed by parts of the build workflow

## Prerequisites

Install these tools on your host machine first:

- [Git](https://git-scm.com/)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (or [Docker Engine](https://docs.docker.com/engine/install/) on Linux)
- [Visual Studio Code](https://code.visualstudio.com/)
- VS Code extension: [Dev Containers](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers) (by Microsoft)

### What is Docker?

Docker runs isolated environments called containers. Each container has its own filesystem and toolchain.

For DSF, Docker avoids "works on my machine" problems by giving everyone the same Linux build environment.

### What is VS Code Dev Containers?

The Dev Containers extension lets VS Code:

- Build the container image from .devcontainer/Dockerfile
- Start the container
- Reopen your project inside that container

When this is done, terminals in VS Code run inside the container.

## Install Required Software

### 1. Git

Download from [git-scm.com/downloads](https://git-scm.com/downloads) or install from your package manager.

Verify:

```bash
git --version
```

### 2. Docker

Install Docker Desktop:
[docker.com/products/docker-desktop](https://www.docker.com/products/docker-desktop/)

Linux users can install Docker Engine instead:
[docs.docker.com/engine/install](https://docs.docker.com/engine/install/)

Verify:

```bash
docker --version
```

### 3. Visual Studio Code

Install VS Code:
[code.visualstudio.com](https://code.visualstudio.com/)

Then install the Dev Containers extension:

1. Open VS Code.
2. Open Extensions.
3. Search for Dev Containers.
4. Install Dev Containers (publisher: Microsoft).

## Clone the Repository

Open a terminal and run:

```bash
git clone https://github.com/Duet3D/DuetSoftwareFramework.git
cd DuetSoftwareFramework
```

If you need a specific branch, check it out before building:

```bash
git checkout v3.7-andy
```

## Open the Project in VS Code

From a terminal inside the repository:

```bash
code .
```

Or use File -> Open Folder and select the DuetSoftwareFramework folder.

## Open in Dev Container

When VS Code opens this repository, it should detect [.devcontainer/devcontainer.json](.devcontainer/devcontainer.json) and prompt:

"Folder contains a Dev Container configuration file. Reopen in Container?"

Choose Reopen in Container.

If you do not see the prompt:

1. Open the Command Palette.
2. Run Dev Containers: Reopen in Container.

On first run, VS Code will:

1. Build the Docker image from [.devcontainer/Dockerfile](.devcontainer/Dockerfile).
2. Start the container.
3. Run post-create setup, including restore of the solution.

The first build can take several minutes. Later starts are usually much faster due to Docker layer caching.

## First Build

After the container finishes starting, open a new terminal in VS Code.

Build the whole solution:

```bash
cd src
dotnet build DuetSoftwareFramework.sln
```

The solution file is [src/DuetSoftwareFramework.sln](src/DuetSoftwareFramework.sln).

If build succeeds, your environment is ready.

## Build Using VS Code Tasks

This repository includes predefined tasks in [.vscode/tasks.json](.vscode/tasks.json).

To run a task:

1. Press Ctrl+Shift+P.
2. Run Tasks: Run Task.
3. Select a task.

Common tasks:

- Build All
- Build and Deploy
- Restart DSF Services
- Build individual components (for example Build DuetControlServer)

The Build All task produces binaries under the local build/ directory.

## Deploy to a Remote DuetPi/SBC

The VS Code tasks include a deployment flow that uses SSH and rsync.

Typical sequence:

1. Run Build and Deploy.
2. Enter the target IP address when prompted.

What the task flow does:

1. Stops DSF services on the target.
2. Builds components locally into build/.
3. Copies build output to /opt/dsf/bin/ on the target via rsync.
4. Starts DSF services again.

Notes:

- The remote commands use the root account by default.
- Ensure SSH access is configured to the target machine.
- Ensure Docker container networking can reach the target IP.

Optional helper task:

- Install vsdbg on Remote

This installs Microsoft vsdbg on the remote machine for debugging scenarios.

The task definitions are in [.vscode/tasks.json](.vscode/tasks.json) if you want to inspect or extend them.

## Working with Git in the Dev Container

Git works normally inside the Dev Container.

Useful commands:

```bash
git status
git log --oneline -10
git diff
git checkout -b feature/my-change
git add .
git commit -m "Describe your change"
git push
```

If authentication fails when pushing:

- Check your remote URL with git remote -v.
- Use HTTPS with a token, or configure SSH keys on your host and ensure forwarding works in your setup.

## Troubleshooting

### Docker is not running

If VS Code cannot open the container, make sure Docker is running on your host.

Quick check:

```bash
docker ps
```

### VS Code does not show Reopen in Container

- Confirm Dev Containers extension is installed.
- Confirm you opened the repository root (where .devcontainer/ exists).

### Container build is slow on first run

This is normal. The first build downloads base images and installs packages.

### dotnet restore or build fails in container

Try rebuilding the container:

1. Open Command Palette.
2. Run Dev Containers: Rebuild Container.

Then retry:

```bash
cd src
dotnet restore DuetSoftwareFramework.sln
dotnet build DuetSoftwareFramework.sln
```

### Deployment task fails with SSH or rsync errors

- Confirm target IP is correct and reachable.
- Confirm SSH login works from the container terminal.
- Confirm the target has DSF service names expected by the task.
- Confirm you have permission to write /opt/dsf/bin/ on the target.

Manual connectivity check:

```bash
ssh root@<target-ip> hostname
```

### build/ output looks stale

Run the clean task first:

1. Tasks: Run Task
2. Clean Build Directory
3. Build All

