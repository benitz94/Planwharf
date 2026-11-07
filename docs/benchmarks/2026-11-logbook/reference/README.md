# logbook.js (reference implementation)

Requires Node.js 18 or later. No dependencies.

```
node logbook.js data init
node logbook.js data stream-create orders
node logbook.js data stream-list
node logbook.js data append orders created '{"total":5}' --id order-1
node logbook.js data read orders --from 1 --count 10
node logbook.js data get orders 1
node logbook.js data head orders
node logbook.js data compact orders 2
node logbook.js data cursor-create billing orders
node logbook.js data poll billing --count 5
node logbook.js data cursor-list
node logbook.js data cursor-delete billing
node logbook.js data import events.jsonl
node logbook.js data status
node logbook.js data stream-delete orders --force
```

Tests: `node --test logbook.test.js`.
