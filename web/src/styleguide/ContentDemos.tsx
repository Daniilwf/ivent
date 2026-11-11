import { ObjectEditor } from '../admin/content/ObjectEditor';
import { ObjectPreview } from '../admin/content/ObjectPreview';
import { contentSchemas } from '../admin/content/contentModel';
import { ru } from '../i18n/ru';
import { demoContentPack, demoContentSchema } from './contentFixtures';

// The styleguide's editor saves nothing
const noop = () => undefined;

const schemas = contentSchemas(demoContentSchema);

/**
 * The content editor (4.12): an item open in the form with a schema error next to its field (dice written in words),
 * the players' view of it, and a name with markup shown as text
 */
export function ContentDemos() {
  const [broken, ...rest] = demoContentPack.objects;
  return (
    <div className="grid gap-4">
      {broken ? (
        <ObjectEditor
          seasonId="demo"
          version={3}
          pack={demoContentPack}
          collection="objects"
          index={0}
          initial={broken}
          schemas={schemas}
          editable
          readOnly={false}
          history={[]}
          onSaved={noop}
          onClose={noop}
        />
      ) : null}
      <div className="grid gap-4 desk:grid-cols-2">
        {rest.map((item) => (
          <ObjectPreview key={String(item.id)} value={item} />
        ))}
      </div>
      <p className="text-sm text-ink-soft">{ru.styleguide.content.markup}</p>
    </div>
  );
}
