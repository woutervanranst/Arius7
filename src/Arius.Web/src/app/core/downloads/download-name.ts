/** The name the browser will save: a single file keeps its own name, several become `alias-yyyy-MM-dd.zip` (local date). */
export function downloadFileName(alias: string, date: Date, paths: string[]): string {
  if (paths.length === 1) return paths[0].slice(paths[0].lastIndexOf('/') + 1);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${alias}-${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}.zip`;
}
