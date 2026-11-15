#!/bin/bash
# Linux compile check (the real build runs on Windows in CI). Prints C# errors only.
cd "$(dirname "$0")"
/opt/dotnet/dotnet build WinMonitor.csproj -c Release -p:WindowsAppSDKSelfContained=false -p:AppxGeneratePriEnabled=false 2>&1 \
  | grep -E ": error (CS|MSB3)" | sed -e 's|^/home/claude/WinMonitor/||' -e 's| \[/home/claude/WinMonitor/WinMonitor.csproj\]||' | sort -u > /tmp/claude-0/-home-claude/6fecb0b5-dff4-5d87-bbe2-9d8625e86404/scratchpad/errs.txt
echo "C# errors: $(wc -l < /tmp/claude-0/-home-claude/6fecb0b5-dff4-5d87-bbe2-9d8625e86404/scratchpad/errs.txt)"
head -${1:-40} /tmp/claude-0/-home-claude/6fecb0b5-dff4-5d87-bbe2-9d8625e86404/scratchpad/errs.txt
