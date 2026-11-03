# Tab completion for the scripts in bash, needs argcomplete: source this file from .bashrc
# bash matches the scripts by name, so only those in scripts/ are run to complete

_dsf_scripts=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)

_dsf_complete() {
	if [[ $(cd "$(dirname "$1")" 2>/dev/null && pwd -P) != "$_dsf_scripts" ]]; then
		return 1
	fi
	compopt +o nospace
	local IFS=$'\013'
	COMPREPLY=($(COMP_LINE=$COMP_LINE COMP_POINT=$COMP_POINT COMP_TYPE=$COMP_TYPE _ARGCOMPLETE_COMP_WORDBREAKS=$COMP_WORDBREAKS _ARGCOMPLETE=1 _ARGCOMPLETE_SHELL=bash _ARGCOMPLETE_SUPPRESS_SPACE=1 "$1" 8>&1 9>&2 1>/dev/null 2>&1))
	if [[ ${COMPREPLY-} =~ [=/:]$ ]]; then
		compopt -o nospace
	fi
}

for _dsf_script in "$_dsf_scripts"/*.py; do
	complete -o nospace -o default -o bashdefault -F _dsf_complete "${_dsf_script##*/}"
done
unset _dsf_script
