import { clsx, type ClassValue } from 'clsx';

/**
 * Merges class name values, filtering out falsy values.
 * A thin wrapper around clsx for consistent class name composition.
 */
export function cn(...inputs: ClassValue[]): string {
  return clsx(inputs);
}
