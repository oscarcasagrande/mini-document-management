/**
 * The configuration resources the interface administers, in one place. Client safe: it holds names and
 * paths only, so both the proxy (server) and the pages (server and client) agree on what is allowed.
 */
export const configResources = [
  "product-services",
  "retention-policies",
  "storage-repositories",
  "webhook-subscriptions",
] as const;

export type ConfigResource = (typeof configResources)[number];

export function isConfigResource(value: string): value is ConfigResource {
  return (configResources as readonly string[]).includes(value);
}
