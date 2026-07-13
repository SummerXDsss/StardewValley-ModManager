import { Typography } from "antd";

interface PageTitleProps {
  title: string;
  subtitle: string;
}

export function PageTitle({ title, subtitle }: PageTitleProps) {
  return (
    <div className="page-title">
      <Typography.Title level={1}>{title}</Typography.Title>
      <p>{subtitle}</p>
    </div>
  );
}
