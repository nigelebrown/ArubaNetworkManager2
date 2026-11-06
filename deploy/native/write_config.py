#!/usr/bin/env python3
"""Write sensitive JSON using SQL connection string on stdin, never on command line."""
import json
import os
from pathlib import Path
import sys

if len(sys.argv) != 6:
    raise SystemExit("internal: write_config.py PATH HOST ADMIN KEY_DIR PFX")
file, host, admin, key_dir, cert = sys.argv[1:]
connection = sys.stdin.readline().rstrip("\r\n")
if not connection:
    raise SystemExit("SQL Server connection string is empty")
settings={
    "AllowedHosts":host,
    "ConnectionStrings":{"NetworkManager":connection},
    "Jcf":{
        "DemoMode":False,
        "SampleData":{"Enabled":True},
        "BootstrapAdmin":{"Username":admin,"PasswordFile":"/etc/jcfnetwork-v091/secrets/initial-admin-password"},
        "DataProtection":{"KeysDirectory":key_dir,"CertificatePath":cert,"CertificatePasswordFile":"/etc/jcfnetwork-v091/secrets/keyring-password"},
        "Polling":{"Enabled":False},
        "EnableDeviceWrites":False,
        "LiveLabWrites":{"Enabled":False},
        "DeviceWrites":{"Enabled":False,"AllowedCidrs":[],"AllowedIps":[],"AllowClusterWide":False},
        "PilotAcceptance":{"Enabled":False,"AllowedDeviceIps":[]},
        "AllowedManagementCidrs":["192.0.2.0/24","198.51.100.0/24"],
        "AnalysisIntervalMinutes":60
    }
}
old_umask=os.umask(0o077)
try:
    path=Path(file)
    with path.open("w",encoding="utf-8") as out:
        json.dump(settings,out,indent=2)
        out.write("\n")
finally:
    os.umask(old_umask)
