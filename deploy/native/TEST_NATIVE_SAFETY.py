from pathlib import Path
import json
import unittest
ROOT=Path(__file__).resolve().parents[2]

class NativeDeploymentSafety(unittest.TestCase):
    def test_production_real_identity_and_sql(self):
        s=(ROOT/'src/Jcf.NetworkManager/Program.cs').read_text()
        self.assertIn('builder.Services.AddIdentity<OperatorUser',s)
        self.assertIn('opts=>opts.EnableRetryOnFailure',s)
        self.assertIn('if(!demo) app.MapLocalAuth()',s)
        self.assertIn('if(!demo) await LocalIdentityBootstrap.InitialiseAsync',s)
    def test_real_identity_with_sample_data(self):
        s=(ROOT/'src/Jcf.NetworkManager/Services/DemoSeed.cs').read_text()
        self.assertIn('if(sampleData) await SampleDataService.SeedAsync(db)',s)
        self.assertIn('IsSimulated=true',s)
        self.assertIn('192.0.2.',s)
    def test_simulated_devices_no_network_connector(self):
        s=(ROOT/'src/Jcf.NetworkManager/Services/DeviceService.cs').read_text()
        self.assertIn('device.IsSimulated',s)
        w=(ROOT/'src/Jcf.NetworkManager/Workflow/ChangeWorkflowService.cs').read_text()
        self.assertIn('device.IsSimulated ? simulation.ExecuteAsync',w)
    def test_security_config_fail_closed(self):
        s=(ROOT/'src/Jcf.NetworkManager/Program.cs').read_text()
        self.assertIn('builder.Configuration.AddJsonFile(secureConfigurationFile,optional:false',s)
        self.assertIn('Production requires a persistent Data Protection key directory',s)
        s=(ROOT/'deploy/native/write_config.py').read_text()
        for fragment in ['"DemoMode":False','"Enabled":True','"Polling":{"Enabled":False}','"DeviceWrites":{"Enabled":False']:
            self.assertIn(fragment,s)
    def test_no_docker_and_existing_service_preserved(self):
        s=(ROOT/'deploy/native/ACTIVATE_NATIVE.sh').read_text()
        self.assertNotIn('docker ',s)
        self.assertIn('jcf-network-manager-v091',s)
        self.assertIn('systemctl is-active --quiet jcf-network-manager.service',s)
        self.assertNotIn('systemctl stop jcf-network-manager.service',s)
    def test_tls_firewall_identity_and_hidden_secrets(self):
        s=(ROOT/'deploy/native/ACTIVATE_NATIVE.sh').read_text()
        self.assertIn('openssl x509 -in "$TLS_CERT" -checkhost',s)
        self.assertIn('proxy_pass http://127.0.0.1:5181',s)
        self.assertIn('firewall-cmd --permanent',s)
        self.assertNotIn('firewall-cmd --reload',s)
        self.assertNotIn('python3 - "$SQL"',s)
    def test_project_version(self):
        self.assertIn('<Version>0.9.1</Version>',(ROOT/'src/Jcf.NetworkManager/Jcf.NetworkManager.csproj').read_text())
if __name__ == '__main__':unittest.main(verbosity=2)
