import json
import os
import argparse

SETTINGS_PATH = os.path.join(os.path.dirname(__file__), '..', '.vscode', 'settings.json')

defaults = {
    "dsf.debug.sshUser":     "root",
    "dsf.debug.targetIp":    "",
    "dsf.debug.debuggerPath": "/root/vsdbg/vsdbg",
}

def _strip_jsonc(text):
    # Support VS Code settings.json syntax (comments + trailing commas)
    result = []
    in_string = False
    in_line_comment = False
    in_block_comment = False
    escaped = False
    i = 0

    while i < len(text):
        ch = text[i]
        nxt = text[i + 1] if i + 1 < len(text) else ''

        if in_line_comment:
            if ch == '\n':
                in_line_comment = False
                result.append(ch)
            i += 1
            continue

        if in_block_comment:
            if ch == '*' and nxt == '/':
                in_block_comment = False
                i += 2
            else:
                i += 1
            continue

        if in_string:
            result.append(ch)
            if escaped:
                escaped = False
            elif ch == '\\':
                escaped = True
            elif ch == '"':
                in_string = False
            i += 1
            continue

        if ch == '"':
            in_string = True
            result.append(ch)
            i += 1
            continue

        if ch == '/' and nxt == '/':
            in_line_comment = True
            i += 2
            continue

        if ch == '/' and nxt == '*':
            in_block_comment = True
            i += 2
            continue

        result.append(ch)
        i += 1

    no_comments = ''.join(result)

    # Remove trailing commas before } or ] while respecting strings
    cleaned = []
    in_string = False
    escaped = False
    i = 0

    while i < len(no_comments):
        ch = no_comments[i]
        if in_string:
            cleaned.append(ch)
            if escaped:
                escaped = False
            elif ch == '\\':
                escaped = True
            elif ch == '"':
                in_string = False
            i += 1
            continue

        if ch == '"':
            in_string = True
            cleaned.append(ch)
            i += 1
            continue

        if ch == ',':
            j = i + 1
            while j < len(no_comments) and no_comments[j] in (' ', '\t', '\r', '\n'):
                j += 1
            if j < len(no_comments) and no_comments[j] in (']', '}'):
                i += 1
                continue

        cleaned.append(ch)
        i += 1

    return ''.join(cleaned)

def _load_settings(path):
    if not os.path.exists(path):
        return {}

    with open(path, 'r', encoding='utf-8') as f:
        raw = f.read()

    if not raw.strip():
        return {}

    try:
        return json.loads(raw)
    except (json.JSONDecodeError, ValueError):
        try:
            return json.loads(_strip_jsonc(raw))
        except (json.JSONDecodeError, ValueError):
            return {}

def parse_args():
    parser = argparse.ArgumentParser(description='Create or update settings.json for VS Code Remote Debugging')
    parser.add_argument('--target-ip', type=str, help='Set the target IP address for debugging')
    args = parser.parse_args()
    return args

def main():
    args = parse_args()
    target_ip = args.target_ip

    settings = _load_settings(SETTINGS_PATH)

    for key, value in defaults.items():
        if key not in settings:
            settings[key] = value

    if target_ip:
        settings['dsf.debug.targetIp'] = target_ip
        print(f"Target IP set to: {target_ip}")

    os.makedirs(os.path.dirname(SETTINGS_PATH), exist_ok=True)
    with open(SETTINGS_PATH, 'w', encoding='utf-8') as f:
        json.dump(settings, f, indent=4)
        f.write('\n')

    print(f"Settings saved: {SETTINGS_PATH}")

if __name__ == "__main__":
    main()