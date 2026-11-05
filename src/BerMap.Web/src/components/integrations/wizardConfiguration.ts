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
