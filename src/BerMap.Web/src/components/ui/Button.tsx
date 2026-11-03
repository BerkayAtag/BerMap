import { type ButtonHTMLAttributes, forwardRef } from 'react';
import { buttonClassName, type ButtonSize, type ButtonVariant } from './buttonStyles';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant = 'primary', size = 'md', disabled, ...props }, ref) => {
    return (
      <button
        ref={ref}
        className={buttonClassName(variant, size, className)}
        disabled={disabled}
        {...props}
      />
    );
  },
);

Button.displayName = 'Button';
