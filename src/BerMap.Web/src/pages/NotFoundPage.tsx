import React from 'react';
import { Button } from '@/components/ui/Button';
import { Link } from 'react-router-dom';
import { ArrowLeft } from 'lucide-react';
import { useLocale } from '@/lib/i18n';

export const NotFoundPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';

  return (
    <div className="h-full flex flex-col items-center justify-center text-center p-6">
      <div className="w-16 h-16 rounded-full bg-brand-primary/10 text-brand-primary flex items-center justify-center font-black text-2xl mb-4">
        404
      </div>
      <h2 className="text-xl font-bold text-neutral-900 dark:text-white">
        {isTr ? 'Sayfa Bulunamadı' : 'Page Not Found'}
      </h2>
      <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1 max-w-sm">
        {isTr
          ? 'Aradığınız modül veya görünüm sistemde mevcut değil ya da erişim izniniz bulunmuyor.'
          : 'The module or view you requested does not exist or you lack access permission.'}
      </p>
      <Link to="/" className="mt-6">
        <Button variant="primary" size="sm" className="gap-2 bg-brand-primary">
          <ArrowLeft className="w-4 h-4" />
          {isTr ? 'Ana Panele Dön' : 'Return to Dashboard'}
        </Button>
      </Link>
    </div>
  );
};