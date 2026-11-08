import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  createConfiguration,
  endpointChanged,
  ldapDefaultPort,
  ldapPortForTransport,
  ldapPortMismatch,
  updateConfiguration,
  type ConfigurationFields,
} from './wizardConfiguration.ts';

const vmwareForm: ConfigurationFields = {
  adapterType: 'VMware',
  server: 'vcenter.corp.local',
  port: 443,
  username: 'svc-bermap',
  baseDn: '',
  useSsl: true,
  useStartTls: false,
  allowUntrustedCertificate: false,
  allowPlaintext: false,
  discoverAllDomains: true,
  collectMetrics: true,
  includePoweredOffVms: true,
};

const adForm: ConfigurationFields = {
  ...vmwareForm,
  adapterType: 'ActiveDirectory',
  server: 'dc01.corp.local',
  port: 636,
  baseDn: '',
};

test('create writes the tuning defaults and the form', () => {
  const configuration = createConfiguration(vmwareForm);
  assert.equal(configuration.maxConcurrency, 8);
  assert.equal(configuration.retryCount, 3);
  assert.equal(configuration.timeoutSeconds, 30);
  assert.equal(configuration.server, 'vcenter.corp.local');

  const ad = createConfiguration({ ...adForm, server: '  dc01.corp.local ' });
  assert.equal(ad.pageSize, 500);
  assert.equal(ad.server, 'dc01.corp.local');
  assert.equal(ad.baseDn, null);
});

test('an edit without configuration changes keeps the saved configuration as it is', () => {
  const saved = {
    server: 'vcenter.corp.local',
    port: 443,
    username: 'svc-bermap',
    maxConcurrency: 16,
    retryCount: 7,
    timeoutSeconds: 120,
    ignoreSslErrors: true,
  };
  assert.deepEqual(updateConfiguration(saved, vmwareForm, vmwareForm), saved);
});

test('an edit writes only the changed fields over the saved configuration', () => {
  const saved = { server: 'vcenter.corp.local', port: 443, username: 'svc-bermap', maxConcurrency: 16, retryCount: 7 };
  const edited = { ...vmwareForm, server: 'vcenter02.corp.local', includePoweredOffVms: false };
  assert.deepEqual(updateConfiguration(saved, vmwareForm, edited), {
    ...saved,
    server: 'vcenter02.corp.local',
    includePoweredOffVms: false,
  });
});

test('an edit does not add defaults the saved configuration lacks', () => {
  const saved = { server: 'dc01.corp.local', port: 636, username: 'svc' };
  const prefilled = { ...adForm, username: 'svc' };
  const result = updateConfiguration(saved, prefilled, { ...prefilled, baseDn: 'DC=corp,DC=local' });
  assert.deepEqual(result, { ...saved, baseDn: 'DC=corp,DC=local' });
  assert.equal('pageSize' in result, false);
  assert.equal('useSsl' in result, false);
});

test('an edit tolerates a missing saved configuration', () => {
  assert.deepEqual(updateConfiguration(undefined, vmwareForm, { ...vmwareForm, port: 8443 }), { port: 8443 });
});

test('switching LDAPS and StartTLS/plaintext moves a standard port with the transport', () => {
  assert.equal(ldapPortForTransport(636, false), 389);
  assert.equal(ldapPortForTransport(389, true), 636);
  assert.equal(ldapPortForTransport(636, true), 636);
  assert.equal(ldapDefaultPort(true), 636);
  assert.equal(ldapDefaultPort(false), 389);
});

test('a port the user typed is kept when the transport changes', () => {
  assert.equal(ldapPortForTransport(3269, false), 3269);
  assert.equal(ldapPortForTransport(10389, true), 10389);
});

test('ldapPortMismatch flags StartTLS on 636 and LDAPS on 389 only', () => {
  assert.equal(ldapPortMismatch(636, false), true);
  assert.equal(ldapPortMismatch(389, true), true);
  assert.equal(ldapPortMismatch(389, false), false);
  assert.equal(ldapPortMismatch(3268, false), false);
});

test('endpointChanged: server, port, username and TLS changes need a new secret', () => {
  assert.equal(endpointChanged(vmwareForm, { ...vmwareForm }), false);
  assert.equal(endpointChanged(vmwareForm, { ...vmwareForm, collectMetrics: false }), false);
  assert.equal(endpointChanged(vmwareForm, { ...vmwareForm, server: 'evil.example' }), true);
  assert.equal(endpointChanged(vmwareForm, { ...vmwareForm, server: ' vcenter.corp.local ' }), false);
  assert.equal(endpointChanged(vmwareForm, { ...vmwareForm, port: 8443 }), true);
  assert.equal(endpointChanged(vmwareForm, { ...vmwareForm, username: 'other' }), true);
  assert.equal(endpointChanged(vmwareForm, { ...vmwareForm, allowUntrustedCertificate: true }), true);
  // VMware has no LDAP transport keys; their form values do not count.
  assert.equal(endpointChanged(vmwareForm, { ...vmwareForm, useStartTls: true }), false);
  assert.equal(endpointChanged(adForm, { ...adForm, useSsl: false, useStartTls: true }), true);
  assert.equal(endpointChanged(adForm, { ...adForm, allowPlaintext: true }), true);
  assert.equal(endpointChanged(adForm, { ...adForm, baseDn: 'DC=corp,DC=local' }), false);
});
