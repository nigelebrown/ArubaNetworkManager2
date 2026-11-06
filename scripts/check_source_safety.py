#!/usr/bin/env python3
"""Legacy path: use the current enterprise source verifier."""
import runpy
from pathlib import Path
runpy.run_path(str(Path(__file__).resolve().parent/'verify_enterprise_source.py'),run_name='__main__')
