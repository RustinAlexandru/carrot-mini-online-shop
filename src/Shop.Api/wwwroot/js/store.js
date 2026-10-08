export function createStore() {
  const selectedItems = new Map();
  return {
    get items() { return [...selectedItems].map(([productId, quantity]) => ({ productId, quantity })); },
    select(productId, quantity) {
      if (!productId || !Number.isInteger(quantity) || quantity < 1 || quantity > 10000) {
        throw new RangeError('A product and quantity from 1 to 10000 are required.');
      }
      selectedItems.set(productId, quantity);
    },
    remove(productId) { selectedItems.delete(productId); },
    clear() { selectedItems.clear(); }
  };
}
