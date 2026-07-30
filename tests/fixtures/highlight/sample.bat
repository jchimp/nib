@echo off
REM nib highlight fixture
setlocal enabledelayedexpansion

set "TARGET=%~1"
if "%TARGET%"=="" (
    echo usage: sample.bat ^<path^>
    exit /b 1
)

for %%F in ("%TARGET%\*.conf") do echo checking %%F
