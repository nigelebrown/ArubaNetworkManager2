#!/usr/bin/env python3
"""Pure stdlib client safety tests. Do not confuse these with hardware acceptance."""
import importlib.util
from pathlib import Path
import unittest

script=Path(__file__).with_name('VERIFY_REAL_ARUBA_SQL_PILOT.py')
spec=importlib.util.spec_from_file_location('pilot_client',script)
module=importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class ClientPolicyTests(unittest.TestCase):
    def test_extracts_login_antiforgery_token(self):
        p=module.TokenParser()
        p.feed('''<form><input name='username'><input type='hidden' name='__RequestVerificationToken' value='A123XY'><input name='password'></form>''')
        self.assertEqual(p.token,'A123XY')
    def test_no_token_is_unknown_not_a_guess(self):
        p=module.TokenParser();p.feed('<input name="username">')
        self.assertIsNone(p.token)
    def test_http_login_is_prohibited(self):
        with self.assertRaises(ValueError):module.Client('http://127.0.0.1:1234')
    def test_embedded_url_credentials_are_prohibited(self):
        with self.assertRaises(ValueError):module.Client('https://admin:password@example.test')
    def test_https_base_is_supported_without_network_io(self):
        c=module.Client('https://pilot.example.internal')
        self.assertEqual(c.origin,('https','pilot.example.internal'))
        self.assertIsNone(c.csrf)
    def test_cannot_submit_authenticated_json_mutation_without_csrf_token(self):
        c=module.Client('https://pilot.example.internal')
        with self.assertRaises(RuntimeError):c.call('/api/v1/devices','POST',{'x':1})

if __name__=='__main__': unittest.main(verbosity=2)
