import "server-only";

import { fetchJson } from "./api";
import type { ConfigResource } from "./config-resources";
import type { PagedResponse } from "./contracts";

/** Every item of a configuration resource. These lists are small by nature (products, policies, repositories). */
export async function listConfig<T>(resource: ConfigResource): Promise<T[]> {
  const page = await fetchJson<PagedResponse<T>>(`/api/v1/${resource}?pageSize=100`);

  return page.items;
}
