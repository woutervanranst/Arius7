import { deleteScratchContainers } from './azure';

/** Removes the throwaway containers the @write specs archived into, so they don't accumulate in Azure. */
export default async function globalTeardown(): Promise<void> {
  const deleted = await deleteScratchContainers();
  if (deleted) console.log(`[global-teardown] deleted ${deleted} scratch container(s)`);
}
