import { ArrowDown, ArrowUp, Plus, Trash2 } from 'lucide-react';
import { ru } from '../../i18n/ru';
import { Button, IconButton } from '../../ui/Button';
import { Checkbox, Field, Select, TextArea } from '../../ui/Field';
import { isSchema, variantType, type JsonSchema } from '../ruleSchema';
import type { ContentSchemas, Problem } from './contentModel';
import { getIn, pathText, setIn, type Segment } from './paths';
import { actionTemplate, actionTypes, type ActionType, type Json, type PackIds } from './templates';

const t = ru.admin.content;
const f = t.fields;

/** A typed whole number, or the text as it is (the schema then says what is wrong), or nothing */
function wholeOr(text: string): unknown {
  const clean = text.trim().replace('−', '-');
  if (clean === '') return undefined;
  return /^-?\d+$/u.test(clean) ? Number(clean) : text;
}

const shown = (value: unknown) =>
  value === undefined || value === null
    ? ''
    : typeof value === 'string'
      ? value
      : JSON.stringify(value);

type FormProps = {
  value: Json;
  problems: readonly Problem[];
  schemas: ContentSchemas;
  ids: PackIds;
  disabled: boolean;
  onChange: (next: Json) => void;
};

/** The definition of an object as a form (D-495): the common fields, the effect's trigger, target and duration, the actions */
export function ObjectForm({ value, problems, schemas, ids, disabled, onChange }: FormProps) {
  const error = (path: readonly Segment[]) => {
    const at = pathText(path);
    const texts = problems.filter((p) => p.path === at).map((p) => p.text);
    return texts.length > 0 ? texts.join(' ') : undefined;
  };
  const set = (path: readonly Segment[], next: unknown) => {
    onChange(setIn(value, path, next));
  };
  const text = (path: readonly Segment[], label: string, hint?: string, testId?: string) => (
    <Field
      label={label}
      {...(hint ? { hint } : {})}
      value={shown(getIn(value, path))}
      error={error(path)}
      disabled={disabled}
      spellCheck={false}
      data-testid={testId ?? `content-field-${pathText(path)}`}
      onChange={(e) => {
        set(path, e.target.value);
      }}
    />
  );
  const choice = (
    path: readonly Segment[],
    label: string,
    words: Readonly<Record<string, string>>,
    values?: readonly string[],
  ) => (
    <EnumSelect
      label={label}
      value={getIn(value, path)}
      values={values ?? Object.keys(words)}
      words={words}
      error={error(path)}
      disabled={disabled}
      testId={`content-field-${pathText(path)}`}
      onChange={(next) => {
        set(path, next);
      }}
    />
  );
  const flag = (path: readonly Segment[], label: string) => (
    <Checkbox
      label={label}
      checked={getIn(value, path) === true}
      disabled={disabled}
      data-testid={`content-field-${pathText(path)}`}
      onChange={(e) => {
        set(path, e.target.checked ? true : undefined);
      }}
    />
  );

  const effect = isSchema(value.effect) ? (value.effect as Json) : null;
  const duration = effect && isSchema(effect.duration) ? (effect.duration as Json) : null;
  const durationKind = duration
    ? (['uses', 'hours', 'runs', 'untilTriggered'].find((k) => k in duration) ?? 'none')
    : 'none';
  const actions = effect && Array.isArray(effect.actions) ? (effect.actions as unknown[]) : null;

  return (
    <div className="grid gap-5" data-testid="content-form">
      <div className="grid gap-4 desk:grid-cols-2">
        {text(['id'], f.id ?? '', f.idHint)}
        {choice(['kind'], f.kind ?? '', t.values.kind ?? {})}
        {text(['name'], f.name ?? '')}
        {choice(['rarity'], f.rarity ?? '', t.values.rarity ?? {})}
        <Field
          label={f.price ?? ''}
          hint={f.priceHint ?? ''}
          inputMode="numeric"
          value={shown(value.price)}
          error={error(['price'])}
          disabled={disabled}
          data-testid="content-field-price"
          onChange={(e) => {
            set(['price'], wholeOr(e.target.value));
          }}
        />
        {choice(['window'], f.window ?? '', t.values.window ?? {})}
      </div>
      <TextArea
        label={f.description ?? ''}
        value={shown(value.description)}
        rows={3}
        error={error(['description'])}
        disabled={disabled}
        data-testid="content-field-description"
        onChange={(e) => {
          set(['description'], e.target.value);
        }}
      />
      <div className="flex flex-wrap gap-x-6">
        {flag(['hostile'], f.hostile ?? '')}
        {flag(['stackable'], f.stackable ?? '')}
        {flag(['manual'], f.manual ?? '')}
      </div>
      {error(['effect']) ? (
        <p role="alert" className="text-sm font-medium text-danger">
          {error(['effect'])}
        </p>
      ) : null}

      <fieldset className="grid gap-4 rounded-md border-2 border-muted p-4" disabled={disabled}>
        <legend className="px-1 font-display text-lg font-heavy">{f.effect}</legend>
        <div className="grid gap-4 desk:grid-cols-2">
          {choice(['effect', 'trigger'], f.trigger ?? '', t.values.trigger ?? {})}
          {choice(['effect', 'intercept'], f.intercept ?? '', t.values.intercept ?? {})}
          {choice(['effect', 'target', 'selector'], f.target ?? '', t.values.selector ?? {})}
          {choice(['effect', 'target', 'among'], f.among ?? '', t.values.among ?? {})}
          <Select
            label={f.duration ?? ''}
            value={durationKind}
            error={error(['effect', 'duration'])}
            data-testid="content-field-effect.duration"
            onChange={(e) => {
              const kind = e.target.value;
              set(
                ['effect', 'duration'],
                kind === 'none' ? undefined : { [kind]: kind === 'untilTriggered' ? true : 1 },
              );
            }}
          >
            {Object.entries(t.durations).map(([kind, word]) => (
              <option key={kind} value={kind}>
                {word}
              </option>
            ))}
          </Select>
          {durationKind !== 'none' && durationKind !== 'untilTriggered' ? (
            <Field
              label={f.durationValue ?? ''}
              inputMode="numeric"
              value={shown(duration?.[durationKind])}
              error={error(['effect', 'duration', durationKind])}
              data-testid="content-field-effect.duration.value"
              onChange={(e) => {
                set(['effect', 'duration', durationKind], wholeOr(e.target.value));
              }}
            />
          ) : null}
        </div>
        {error(['effect', 'target']) ? (
          <p role="alert" className="text-sm font-medium text-danger">
            {error(['effect', 'target'])}
          </p>
        ) : null}
        {flag(['effect', 'target', 'excludeSelf'], f.excludeSelf ?? '')}
        {effect && 'condition' in effect ? (
          <InJson
            label={f.condition ?? ''}
            value={effect.condition}
            error={error(['effect', 'condition'])}
          />
        ) : null}
        {effect && 'outcomes' in effect ? (
          <InJson
            label={f.outcomes ?? ''}
            value={effect.outcomes}
            error={error(['effect', 'outcomes'])}
          />
        ) : null}

        <div className="grid gap-3">
          <h3 className="font-bold">{f.actions}</h3>
          {error(['effect', 'actions']) ? (
            <p role="alert" className="text-sm font-medium text-danger">
              {error(['effect', 'actions'])}
            </p>
          ) : null}
          {(actions ?? []).map((action, i) => (
            <ActionCard
              // The place is the identity: actions have no ids, and moving one moves its fields with it
              key={i}
              index={i}
              count={actions?.length ?? 0}
              action={action}
              schemas={schemas}
              ids={ids}
              error={error}
              disabled={disabled}
              onChange={(next) => {
                set(['effect', 'actions', i], next);
              }}
              onMove={(to) => {
                const list = [...(actions ?? [])];
                const [moved] = list.splice(i, 1);
                list.splice(to, 0, moved);
                set(['effect', 'actions'], list);
              }}
              onRemove={() => {
                set(
                  ['effect', 'actions'],
                  (actions ?? []).filter((_, j) => j !== i),
                );
              }}
            />
          ))}
        </div>
      </fieldset>
    </div>
  );
}

function ActionCard({
  index,
  count,
  action,
  schemas,
  ids,
  error,
  disabled,
  onChange,
  onMove,
  onRemove,
}: {
  index: number;
  count: number;
  action: unknown;
  schemas: ContentSchemas;
  ids: PackIds;
  error: (path: readonly Segment[]) => string | undefined;
  disabled: boolean;
  onChange: (next: unknown) => void;
  onMove: (to: number) => void;
  onRemove: () => void;
}) {
  const record = isSchema(action) ? (action as Json) : {};
  const type = typeof record.type === 'string' ? record.type : '';
  const base: Segment[] = ['effect', 'actions', index];
  const variant = schemas.action?.anyOf?.find((v) => variantType(v) === type) ?? null;
  const name = t.actions[type]?.name ?? type;
  const n = index + 1;
  const fields = variant
    ? Object.entries(variant.properties ?? {}).filter(([key]) => key !== 'type' && key !== 'target')
    : Object.keys(record)
        .filter((key) => key !== 'type' && key !== 'target')
        .map((key) => [key, true] as const);

  return (
    <div
      className="grid gap-3 rounded-md bg-page p-3"
      data-testid={`content-action-${index}`}
      role="group"
      aria-label={t.action(n, name)}
    >
      <div className="flex flex-wrap items-center gap-2">
        <p className="mr-auto font-bold">{t.action(n, name)}</p>
        <IconButton
          label={t.moveUp(n)}
          disabled={disabled || index === 0}
          onClick={() => {
            onMove(index - 1);
          }}
        >
          <ArrowUp size={20} aria-hidden />
        </IconButton>
        <IconButton
          label={t.moveDown(n)}
          disabled={disabled || index === count - 1}
          onClick={() => {
            onMove(index + 1);
          }}
        >
          <ArrowDown size={20} aria-hidden />
        </IconButton>
        <IconButton
          label={t.removeAction(n)}
          disabled={disabled}
          data-testid={`content-action-${index}-remove`}
          onClick={onRemove}
        >
          <Trash2 size={20} aria-hidden />
        </IconButton>
      </div>
      {t.actions[type] ? <p className="text-sm text-ink-soft">{t.actions[type].hint}</p> : null}
      {error(base) ? (
        <p role="alert" className="text-sm font-medium text-danger">
          {error(base)}
        </p>
      ) : null}
      <div className="grid gap-3 desk:grid-cols-2">
        <EnumSelect
          label={f.actionType ?? ''}
          value={type}
          values={actionTypes}
          words={Object.fromEntries(actionTypes.map((a) => [a, t.actions[a]?.name ?? a]))}
          required
          error={error([...base, 'type'])}
          disabled={disabled}
          testId={`content-field-${pathText([...base, 'type'])}`}
          onChange={(next) => {
            if (typeof next === 'string') onChange(actionTemplate(next as ActionType, ids));
          }}
        />
        <EnumSelect
          label={ru.admin.content.fields.actionTarget ?? ''}
          value={record.target}
          values={['self']}
          words={t.values.target ?? {}}
          error={error([...base, 'target'])}
          disabled={disabled}
          testId={`content-field-${pathText([...base, 'target'])}`}
          onChange={(next) => {
            onChange(setIn(record, ['target'], next));
          }}
        />
        {fields.map(([key, schema]) => (
          <ActionField
            key={key}
            name={key}
            path={[...base, key]}
            schema={isSchema(schema) ? schema : null}
            required={variant?.required?.includes(key) ?? false}
            value={record[key]}
            error={error([...base, key])}
            disabled={disabled}
            onChange={(next) => {
              onChange(setIn(record, [key], next));
            }}
          />
        ))}
      </div>
    </div>
  );
}

/** One parameter of an action, by its schema: a list to pick from, a number or dice, text, a flag, or JSON */
function ActionField({
  name,
  path,
  schema,
  required,
  value,
  error,
  disabled,
  onChange,
}: {
  name: string;
  path: readonly Segment[];
  schema: JsonSchema | null;
  required: boolean;
  value: unknown;
  error: string | undefined;
  disabled: boolean;
  onChange: (next: unknown) => void;
}) {
  const testId = `content-field-${pathText(path)}`;
  const types =
    schema?.type === undefined ? [] : Array.isArray(schema.type) ? schema.type : [schema.type];
  if (schema?.enum) {
    const values = schema.enum.filter((v): v is string => typeof v === 'string');
    return (
      <EnumSelect
        label={name}
        value={value}
        values={values}
        words={t.values[name] ?? {}}
        required={required}
        error={error}
        disabled={disabled}
        testId={testId}
        onChange={onChange}
      />
    );
  }
  if (types.includes('boolean'))
    return (
      <Checkbox
        label={name}
        checked={value === true}
        disabled={disabled}
        data-testid={testId}
        onChange={(e) => {
          onChange(e.target.checked ? true : undefined);
        }}
      />
    );
  const numeric =
    schema?.pattern !== undefined || types.includes('integer') || types.includes('number');
  if (schema && (numeric || types.includes('string')))
    return (
      <Field
        label={name}
        {...(schema.pattern !== undefined ? { hint: t.valueHint } : {})}
        value={shown(value)}
        error={error}
        disabled={disabled}
        spellCheck={false}
        data-testid={testId}
        onChange={(e) => {
          const text = e.target.value;
          onChange(numeric ? wholeOr(text) : text === '' && !required ? undefined : text);
        }}
      />
    );
  return <InJson label={name} value={value} error={error} />;
}

/** A pick from the content's values: the word and the code the JSON keeps («после броска кубов (afterDice)») */
function EnumSelect({
  label,
  value,
  values,
  words,
  required = false,
  error,
  disabled,
  testId,
  onChange,
}: {
  label: string;
  value: unknown;
  values: readonly string[];
  words: Readonly<Record<string, string>>;
  required?: boolean;
  error: string | undefined;
  disabled: boolean;
  testId: string;
  onChange: (next: string | undefined) => void;
}) {
  const current = typeof value === 'string' ? value : '';
  // A value the list does not know stays visible, so the admin sees what the schema complains about
  const options = current !== '' && !values.includes(current) ? [...values, current] : values;
  return (
    <Select
      label={label}
      value={current}
      error={error}
      disabled={disabled}
      data-testid={testId}
      onChange={(e) => {
        onChange(e.target.value === '' ? undefined : e.target.value);
      }}
    >
      {required && current !== '' ? null : <option value="">{t.fields.none}</option>}
      {options.map((v) => (
        <option key={v} value={v}>
          {words[v] ? `${words[v]} (${v})` : v}
        </option>
      ))}
    </Select>
  );
}

/** A part the form does not edit: shown as JSON, edited in the JSON view */
function InJson({
  label,
  value,
  error,
}: {
  label: string;
  value: unknown;
  error: string | undefined;
}) {
  return (
    <div className="grid gap-1">
      <p className="text-sm font-bold">{label}</p>
      <code className="block rounded-md bg-muted px-3 py-2 font-code text-sm break-all whitespace-pre-wrap">
        {JSON.stringify(value)}
      </code>
      <p className="text-sm text-ink-soft">{t.inJson}</p>
      {error ? (
        <p role="alert" className="text-sm font-medium text-danger">
          {error}
        </p>
      ) : null}
    </div>
  );
}

/** A wheel of objects as a form: its name and its entries with weights */
export function WheelForm({
  value,
  problems,
  ids,
  disabled,
  onChange,
}: Omit<FormProps, 'schemas'>) {
  const error = (path: readonly Segment[]) => {
    const at = pathText(path);
    const texts = problems.filter((p) => p.path === at).map((p) => p.text);
    return texts.length > 0 ? texts.join(' ') : undefined;
  };
  const entries = Array.isArray(value.entries) ? (value.entries as unknown[]) : [];
  const set = (path: readonly Segment[], next: unknown) => {
    onChange(setIn(value, path, next));
  };
  return (
    <div className="grid gap-4" data-testid="content-form">
      <div className="grid gap-4 desk:grid-cols-2">
        <Field
          label={f.id ?? ''}
          hint={f.idHint ?? ''}
          value={shown(value.id)}
          error={error(['id'])}
          disabled={disabled}
          data-testid="content-field-id"
          onChange={(e) => {
            set(['id'], e.target.value);
          }}
        />
        <Field
          label={f.wheelName ?? ''}
          value={shown(value.name)}
          error={error(['name'])}
          disabled={disabled}
          data-testid="content-field-name"
          onChange={(e) => {
            set(['name'], e.target.value);
          }}
        />
      </div>
      <fieldset className="grid gap-3" disabled={disabled}>
        <legend className="mb-1 font-bold">{f.entries}</legend>
        {error(['entries']) ? (
          <p role="alert" className="text-sm font-medium text-danger">
            {error(['entries'])}
          </p>
        ) : null}
        {entries.map((entry, i) => {
          const record = isSchema(entry) ? (entry as Json) : {};
          return (
            <div key={i} className="grid grid-cols-[minmax(0,1fr)_auto_auto] items-end gap-2">
              <EnumSelect
                label={t.entry(i + 1)}
                value={record.objectId}
                values={ids.objects}
                words={{}}
                required
                error={error(['entries', i, 'objectId'])}
                disabled={disabled}
                testId={`content-field-entries[${i}].objectId`}
                onChange={(next) => {
                  set(['entries', i, 'objectId'], next);
                }}
              />
              <Field
                label={f.weight ?? ''}
                className="w-28"
                inputMode="numeric"
                value={shown(record.weight)}
                error={error(['entries', i, 'weight'])}
                disabled={disabled}
                data-testid={`content-field-entries[${i}].weight`}
                onChange={(e) => {
                  set(['entries', i, 'weight'], wholeOr(e.target.value));
                }}
              />
              <IconButton
                label={t.removeEntry(i + 1)}
                disabled={disabled}
                onClick={() => {
                  set(
                    ['entries'],
                    entries.filter((_, j) => j !== i),
                  );
                }}
              >
                <Trash2 size={20} aria-hidden />
              </IconButton>
            </div>
          );
        })}
        <div>
          <Button
            icon={<Plus size={20} aria-hidden />}
            disabled={disabled || ids.objects.length === 0}
            onClick={() => {
              set(['entries'], [...entries, { objectId: ids.objects[0], weight: 1 }]);
            }}
          >
            {t.addEntry}
          </Button>
        </div>
      </fieldset>
    </div>
  );
}
