#!/bin/zsh
# Finder opens .command files in Terminal; retain feedback until the user closes it.
SCRIPT_DIRECTORY="${0:A:h}"
if command -v python3 >/dev/null 2>&1; then
  python3 "$SCRIPT_DIRECTORY/wake_pc.py" wake
  wake_result=$?
else
  print 'Wake PC needs Python 3.9 or later. Install Python from python.org, then try again.'
  wake_result=1
fi
print ''
read -r '?Press Return to close…'
exit "$wake_result"
