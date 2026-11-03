import { type HTMLAttributes, forwardRef } from 'react';
import { cn } from '@/lib/cn';

interface CardProps extends HTMLAttributes<HTMLDivElement> {
  padding?: 'none' | 'sm' | 'md' | 'lg';
}

const paddingStyles = {
  none: '',
  sm: 'p-4',
  md: 'p-6',
  lg: 'p-8',
};

export const Card = forwardRef<HTMLDivElement, CardProps>(
  ({ className, padding = 'md', ...props }, ref) => {
    return (
      <div
        ref={ref}
        className={cn(
          'rounded-xl border bg-white shadow-card',
          'border-neutral-200',
          'dark:bg-dark-card dark:border-dark-border dark:shadow-none',
          'transition-colors duration-150',
          paddingStyles[padding],
          className,
        )}
        {...props}
      />
    );
  },
);

Card.displayName = 'Card';
