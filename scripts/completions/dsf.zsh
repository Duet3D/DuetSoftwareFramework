# Tab completion for the scripts in zsh, needs argcomplete and compinit: source this file from .zshrc
# zsh matches the scripts by name, so only those in scripts/ are run to complete

typeset -g _dsf_scripts=${${(%):-%x}:A:h:h}

_dsf_complete() {
	[[ ${words[1]:A:h} == $_dsf_scripts ]] || return 1
	local IFS=$'\013'
	local -a completions
	completions=($(COMP_LINE=$BUFFER COMP_POINT=$CURSOR _ARGCOMPLETE=1 _ARGCOMPLETE_SHELL=zsh _ARGCOMPLETE_SUPPRESS_SPACE=1 ${words[1]} 8>&1 9>&2 1>/dev/null 2>&1))
	_describe ${words[1]} completions -o nosort
}

compdef _dsf_complete $_dsf_scripts/*.py(:t)
