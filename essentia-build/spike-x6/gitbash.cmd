@rem Runs a spike-x6 script under Git Bash from a scheduled task (schtasks /tr
@rem cannot carry the quoted "Program Files" path through PowerShell reliably).
@rem   gitbash.cmd <script.sh> [args...]
@"C:\Program Files\Git\bin\bash.exe" %*
