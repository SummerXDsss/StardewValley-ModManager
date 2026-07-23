interface StatusItemProps {
  label: string;
  value: string;
  detail?: string;
  ok: boolean;
  warning?: boolean;
}

export function StatusItem({ label, value, detail, ok, warning }: StatusItemProps) {
  const color = warning ? "warning" : ok ? "success" : "danger";
  const icon = warning
    ? <Warning16Regular />
    : ok
      ? <CheckmarkCircle16Regular />
      : <DismissCircle16Regular />;
  return (
    <div className="status-item">
      <span>{label}</span>
      <div>
        <Badge appearance="tint" color={color} icon={icon}>{value}</Badge>
        {detail && <small>{detail}</small>}
      </div>
    </div>
  );
}
import { Badge } from "@fluentui/react-components";
import { CheckmarkCircle16Regular, DismissCircle16Regular, Warning16Regular } from "@fluentui/react-icons";
