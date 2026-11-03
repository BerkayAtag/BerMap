import { forwardRef } from 'react';
import { Link, type LinkProps } from 'react-router-dom';
import { buttonClassName, type ButtonSize, type ButtonVariant } from './buttonStyles';

interface ButtonLinkProps extends LinkProps {
  variant?: ButtonVariant;
  size?: ButtonSize;
}

/** A router link styled as a button (avoids nesting <button> inside <a>). */
export const ButtonLink = forwardRef<HTMLAnchorElement, ButtonLinkProps>(
  ({ className, variant = 'primary', size = 'md', ...props }, ref) => (
    <Link ref={ref} className={buttonClassName(variant, size, typeof className === 'string' ? className : undefined)} {...props} />
  ),
);

ButtonLink.displayName = 'ButtonLink';
