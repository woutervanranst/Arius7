import { BlobServiceClient, StorageSharedKeyCredential } from '@azure/storage-blob';
import { SCRATCH_PREFIX } from './scratch';

/** Blob service client for the e2e storage account, or null when credentials aren't configured. */
function serviceClient(): BlobServiceClient | null {
  const account = process.env.ARIUS_E2E_ACCOUNT;
  const key = process.env.ARIUS_E2E_KEY;
  if (!account || !key) return null;
  return new BlobServiceClient(
    `https://${account}.blob.core.windows.net`,
    new StorageSharedKeyCredential(account, key),
  );
}

/**
 * Deletes every scratch container (the @write specs' throwaway archive targets) from the test
 * account. The API's repo-delete only drops the registration row, so the containers themselves have
 * to be removed here. No-op when credentials aren't set; ignores containers that race away.
 */
export async function deleteScratchContainers(): Promise<number> {
  const svc = serviceClient();
  if (!svc) return 0;
  let deleted = 0;
  for await (const c of svc.listContainers({ prefix: SCRATCH_PREFIX })) {
    try { await svc.deleteContainer(c.name); deleted++; } catch { /* already gone / racing another run */ }
  }
  return deleted;
}
