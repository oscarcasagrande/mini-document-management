import sys
from pathlib import Path

# Lets the tests import the ``app`` package without installing it.
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
