interface StatusItemProps {
  label: string;
  value: string;
  detail?: string;
  ok: boolean;
  warning?: boolean;
}

export function StatusItem({ label, value, detail, ok, warning }: StatusItemProps) {
  return (
    <div className="status-item">
      <span>{label}</span>
      <div>
        <i className={warning ? "warning" : ok ? "ok" : "bad"} />
        <strong>{value}</strong>
        {detail && <small>{detail}</small>}
      </div>
    </div>
  );
}
