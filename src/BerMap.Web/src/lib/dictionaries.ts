export type TranslationKey =
  | 'dashboard'
  | 'infrastructureMap'
  | 'inventory'
  | 'dependencies'
  | 'impactAnalysis'
  | 'berSim'
  | 'snapshots'
  | 'capacityIntelligence'
  | 'eventsAlerts'
  | 'reports'
  | 'integrations'
  | 'collectors'
  | 'administration'
  | 'auditLogs'
  | 'collapseSidebar'
  | 'expandSidebar'
  | 'systemHealthNormal'
  | 'searchPlaceholder'
  | 'userAdministrator'
  | 'platformAdmin'
  | 'lightMode'
  | 'darkMode'
  | 'locale'
  | 'localeTurkish'
  | 'localeEnglish';

const dictionaries: Record<'tr' | 'en', Record<TranslationKey, string>> = {
  tr: {
    dashboard: 'Dashboard',
    infrastructureMap: 'Altyapı Haritası',
    inventory: 'Envanter',
    dependencies: 'Bağımlılıklar',
    impactAnalysis: 'Etki Analizi',
    berSim: 'BerSim',
    snapshots: 'Snapshot',
    capacityIntelligence: 'Kapasite İstihbaratı',
    eventsAlerts: 'Olaylar & Uyarılar',
    reports: 'Raporlar',
    integrations: 'Entegrasyonlar',
    collectors: 'Collectorlar',
    administration: 'Yönetim',
    auditLogs: 'Denetim Günlükleri',
    collapseSidebar: 'Daralt',
    expandSidebar: 'Genişlet',
    systemHealthNormal: 'Sistem Sağlığı: Normal',
    searchPlaceholder: 'Sunucu, cluster, IP veya servis ara... (⌘K)',
    userAdministrator: 'Sistem Yöneticisi',
    platformAdmin: 'Platform Admin',
    lightMode: 'Açık Tema',
    darkMode: 'Koyu Tema',
    locale: 'Dil',
    localeTurkish: 'Türkçe',
    localeEnglish: 'İngilizce',
  },
  en: {
    dashboard: 'Dashboard',
    infrastructureMap: 'Infrastructure Map',
    inventory: 'Inventory',
    dependencies: 'Dependencies',
    impactAnalysis: 'Impact Analysis',
    berSim: 'BerSim',
    snapshots: 'Snapshots',
    capacityIntelligence: 'Capacity Intelligence',
    eventsAlerts: 'Events & Alerts',
    reports: 'Reports',
    integrations: 'Integrations',
    collectors: 'Collectors',
    administration: 'Administration',
    auditLogs: 'Audit Logs',
    collapseSidebar: 'Collapse',
    expandSidebar: 'Expand',
    systemHealthNormal: 'System Health: Normal',
    searchPlaceholder: 'Search server, cluster, IP or service... (⌘K)',
    userAdministrator: 'System Administrator',
    platformAdmin: 'Platform Admin',
    lightMode: 'Light Mode',
    darkMode: 'Dark Mode',
    locale: 'Language',
    localeTurkish: 'Turkish',
    localeEnglish: 'English',
  },
};

export default dictionaries;