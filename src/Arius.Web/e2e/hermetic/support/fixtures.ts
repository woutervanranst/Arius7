import { test as base } from '@playwright/test';
import { Control } from './control';

/** Every hermetic spec gets a `control` client and a clean app db (reset before the spec runs). */
export const test = base.extend<{ control: Control }>({
  control: async ({ request }, use) => {
    const control = new Control(request);
    await control.reset();
    await use(control);
  },
});

export { expect } from '@playwright/test';
