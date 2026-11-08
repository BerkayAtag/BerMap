/**
 * Adapter configuration built by the integration wizard. Kept free of React and
 * path aliases so it can be tested with `npm test` (node:test).
 */

/** The form fields that end up in the adapter configuration. */
export interface ConfigurationFields {
  adapterType: string;
  server: string;
  port: number;
  username: string;
  baseDn: string;
  useSsl: boolean;
  useStartTls: boolean;
  allowUntrustedCertificate: boolean;
  allowPlaintext: boolean;
  discoverAllDomains: boolean;
  collectMetrics: boolean;
  includePoweredOffVms: boolean;
}

export type AdapterConfiguration = Record<string, unknown>;

/** The keys the wizard shows and edits, with the form's values. */
export function editedConfiguration(form: ConfigurationFields): AdapterConfiguration {
  if (form.adapterType === 'ActiveDirectory') {
    return {
      server: form.server.trim(),
      port: form.port,
      useSsl: form.useSsl,
      useStartTls: form.useStartTls,
      username: form.username.trim(),
      baseDn: form.baseDn.trim() || null,
      allowUntrustedCertificate: form.allowUntrustedCertificate,
      allowPlaintext: form.allowPlaintext,
      discoverAllDomains: form.discoverAllDomains,
    };
  }

  return {
    server: form.server.trim(),
    port: form.port,
    username: form.username.trim(),
    allowUntrustedCertificate: form.allowUntrustedCertificate,
    collectMetrics: form.collectMetrics,
    includePoweredOffVms: form.includePoweredOffVms,
  };
}

/**
 * Tuning keys the wizard does not show, written on create so the stored
 * configuration documents them. The adapters apply the same values when a key
 * is missing; administrators change them through the API.
 */
export function tuningDefaults(adapterType: string): AdapterConfiguration {
  if (adapterType === 'ActiveDirectory') {
    return {
      includeGroups: true,
      includeComputers: true,
      includeOus: true,
      includeSites: true,
      includeTrusts: true,
      pageSize: 500,
      timeoutSeconds: 30,
    };
  }

  return {
    ignoreSslErrors: false,
    useSoapMetrics: true,
    timeoutSeconds: 30,
    maxConcurrency: 8,
    retryCount: 3,
  };
}

/** Configuration for a new integration: defaults plus the form. */
export function createConfiguration(form: ConfigurationFields): AdapterConfiguration {
  return { ...tuningDefaults(form.adapterType), ...editedConfiguration(form) };
}

/**
 * Configuration for an edited integration: the saved configuration with only the
 * fields the user changed in the wizard (compared with the form as it was
 * prefilled) written over it. Tuning keys and untouched fields stay exactly as
 * saved, so a rename does not change the configuration at all.
 */
export function updateConfiguration(
  saved: AdapterConfiguration | null | undefined,
  prefilled: ConfigurationFields,
  form: ConfigurationFields,
): AdapterConfiguration {
  const before = editedConfiguration(prefilled);
  const after = editedConfiguration(form);
  const changed = Object.fromEntries(Object.entries(after).filter(([key, value]) => value !== before[key]));
  return { ...(saved ?? {}), ...changed };
}

/** LDAPS listens on 636 (implicit TLS); StartTLS and plaintext LDAP on 389. */
export const LDAPS_PORT = 636;
export const LDAP_PORT = 389;

export function ldapDefaultPort(useSsl: boolean): number {
  return useSsl ? LDAPS_PORT : LDAP_PORT;
}

/**
 * The port after switching between LDAPS and StartTLS/plaintext: a standard
 * LDAP port follows the transport, a port the user typed (3269, 10636...) stays.
 */
export function ldapPortForTransport(port: number, useSsl: boolean): number {
  return port === LDAPS_PORT || port === LDAP_PORT ? ldapDefaultPort(useSsl) : port;
}

/** True when an LDAP port clearly belongs to the other transport (StartTLS on 636, LDAPS on 389). */
export function ldapPortMismatch(port: number, useSsl: boolean): boolean {
  return useSsl ? port === LDAP_PORT : port === LDAPS_PORT;
}

/**
 * Fields that decide where the stored credential is sent. The server refuses to
 * change them without a new secret (SECRET_REQUIRED_FOR_ENDPOINT_CHANGE), so a
 * changed endpoint cannot be used to replay the saved password elsewhere.
 */
const ENDPOINT_KEYS = ['server', 'port', 'username', 'useSsl', 'useStartTls', 'allowUntrustedCertificate', 'allowPlaintext'] as const;

/** True when the edit changes the server, port, username or TLS settings. */
export function endpointChanged(prefilled: ConfigurationFields, form: ConfigurationFields): boolean {
  const before = editedConfiguration(prefilled);
  const after = editedConfiguration(form);
  return ENDPOINT_KEYS.some((key) => key in after && after[key] !== before[key]);
}
