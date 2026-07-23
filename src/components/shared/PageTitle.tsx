import { Text, Title1 } from "@fluentui/react-components";

interface PageTitleProps {
  title: string;
  subtitle: string;
}

export function PageTitle({ title, subtitle }: PageTitleProps) {
  return (
    <div className="page-title">
      <Title1>{title}</Title1>
      <Text size={300}>{subtitle}</Text>
    </div>
  );
}
