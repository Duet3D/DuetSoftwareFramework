#!/bin/bash

PID=$(ssh -T "${DSF_SSH_USER}@${DSF_TARGET_IP}" "pidof DuetControlServer | awk '{print \$1}'")

echo $PID > "${WORKSPACE_FOLDER}/.vscode/dcs.pid"

echo "DuetControlServer PID: $PID"