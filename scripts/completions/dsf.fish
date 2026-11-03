# Tab completion for the scripts in fish, needs argcomplete: source this file from config.fish
# register-python-argcomplete is not used because fish only matches complete --path against absolute paths

function __dsf_complete -a script
    set -lx _ARGCOMPLETE 1
    set -lx _ARGCOMPLETE_DFS \t
    set -lx _ARGCOMPLETE_IFS \n
    set -lx _ARGCOMPLETE_SUPPRESS_SPACE 1
    set -lx _ARGCOMPLETE_SHELL fish
    set -lx COMP_LINE (commandline -p)
    set -lx COMP_POINT (string length (commandline -cp))
    $script 8>&1 9>&2 1>/dev/null 2>&1
end

for script in (path dirname (path dirname (path resolve (status filename))))/*.py
    complete --path $script -f -a "(__dsf_complete $script)"
end
