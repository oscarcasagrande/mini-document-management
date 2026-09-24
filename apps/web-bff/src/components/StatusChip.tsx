import type { DocumentStatus } from "@/lib/contracts";
import { statusLabel, statusTone } from "@/lib/format";

export function StatusChip({ status }: { status: DocumentStatus }) {
  return (
    <span className={`chip chip--${statusTone(status)}`} title={status}>
      {statusLabel(status)}
    </span>
  );
}
